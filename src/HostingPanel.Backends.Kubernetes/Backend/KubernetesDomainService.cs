using HostingPanel.Providers.PowerDNS;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HostingPanel.Api.Adapters;
using HostingPanel.Api.Persistence;
using HostingPanel.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace HostingPanel.Api;

public sealed class KubernetesDomainService(HostingPanelDbContext db, KubernetesIdentityService identity, KubernetesProvisioningService provisioning, KubernetesOperationQueue operations, HostingOperationNotifications? notifications = null, CertificateWork? certificateWork = null) : IDomains, IAccessibleDomains, IDomainPreview
{
    public async Task<VisibleDomainsResult> ListAccessibleDomains(AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var principal = await identity.AuthenticateAsync(auth, ct);
        if (principal is null) return new(401, null, "UNAUTHORIZED");
        var ids = await identity.VisibleAccountsAsync(principal, ct);
        var query = from domain in db.Domains.AsNoTracking()
                    join owner in db.Accounts.AsNoTracking() on domain.OwnerId equals owner.Id
                    where ids.Contains(owner.Id)
                    orderby domain.Name
                    select new { domain.Id, domain.Name, owner.Username, owner.Kind };
        var rows = await query.ToListAsync(ct);
        var status = await ReadProvisioningAsync(rows.Select(x => x.Id).ToArray(), ct);
        return new VisibleDomainsResult(200, rows.Select(x => new VisibleDomain(x.Name, x.Username, x.Kind)).ToArray(), null)
        {
            Provisioning = rows.ToDictionary(x => x.Name, x => status[x.Id], StringComparer.OrdinalIgnoreCase)
        };
    }
    async Task<VisibleDomainsResult> IAccessibleDomains.ListAsync(AuthenticationHeaderValue auth, CancellationToken ct)
        => await ListAccessibleDomains(auth, ct);

    public async Task<DomainListResult> ListAsync(string? owner, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var principal = await identity.AuthenticateAsync(auth, ct);
        if (principal is null) return new(401, null, "UNAUTHORIZED");
        var ids = await identity.VisibleAccountsAsync(principal, ct);
        var query = from domain in db.Domains.AsNoTracking()
                    join account in db.Accounts.AsNoTracking() on domain.OwnerId equals account.Id
                    where ids.Contains(account.Id) && (owner == null ? account.Id == principal.Id : account.Username == owner)
                    select new { domain.Id, domain.Name };
        var rows = await query.OrderBy(x => x.Name).ToListAsync(ct);
        var status = await ReadProvisioningAsync(rows.Select(x => x.Id).ToArray(), ct);
        return new DomainListResult(200, rows.Select(x => x.Name).ToArray(), null)
        {
            Provisioning = rows.ToDictionary(x => x.Name, x => status[x.Id], StringComparer.OrdinalIgnoreCase)
        };
    }
    public async Task<DomainMutationResult> MutateAsync(DomainMutationRequest input, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var principal = await identity.AuthenticateAsync(auth, ct);
        if (principal is null) return new(401, "UNAUTHORIZED");
        if (!KubernetesInputValidation.ValidDomain(input.Domain) || input.Confirmation != input.Domain) return new(400, "CONFIRMATION_REQUIRED");
        var name = input.Domain.ToLowerInvariant();
        var existing = await db.Domains.FirstOrDefaultAsync(x => x.Name == name, ct);
        Guid changedId;
        if (input.Action == "create")
        {
            if (await db.Configuration.AnyAsync(x => x.Key == "DeletingDomain:" + name, ct)) return new(409, "DOMAIN_DELETION_PENDING");
            if (existing is not null) return new(409, "DOMAIN_EXISTS");
            var created = new HostedDomain { OwnerId = principal.Id, Name = name,
                SettingsJson = JsonSerializer.Serialize(new { php = input.Php, ssl = input.Ssl, cgi = input.Cgi }) };
            changedId = created.Id;
            db.Domains.Add(created);
            if (await provisioning.StageDomainDnsAsync(created, ct) is { } dnsError)
            {
                if (dnsError == "DNS_ZONE_EXISTS") return new(409, dnsError);
                db.ReconciliationOperations.Add(new ReconciliationOperation { ResourceType = "domain-dns", ResourceId = created.Id, Action = "create", LastError = dnsError });
            }
            operations.Schedule("domain", created.Id, "create", new { name });
            await provisioning.ScheduleWebsiteAsync(created, "create", ct);
        }
        else
        {
            if (existing is null) return new(404, "DOMAIN_NOT_FOUND");
            changedId = existing.Id;
            var ids = await identity.VisibleAccountsAsync(principal, ct);
            if (!ids.Contains(existing.OwnerId)) return new(403, "ACCESS_DENIED");
            if (input.Action == "delete")
            {
                var owner = await db.Accounts.SingleAsync(x => x.Id == existing.OwnerId, ct);
                var zones = await db.DnsZones.Where(x => x.DomainId == existing.Id).ToListAsync(ct);
                if (zones.Any(x => x.OwnerId != existing.OwnerId)) return new(409, "DNS_OWNERSHIP_CONFLICT");
                foreach (var zone in zones)
                {
                    db.DnsZones.Remove(zone);
                    operations.Schedule("dns-zone", zone.Id, "delete", new { zone.Name });
                }
                var access = await db.FileAccessAccounts.Where(x => x.DomainId == existing.Id).ToListAsync(ct);
                db.FileAccessAccounts.RemoveRange(access);
                var acme = await db.Configuration.FindAsync([KubernetesCertificateProvider.AcmeKey(existing.Id)], ct);
                if (acme is not null) db.Configuration.Remove(acme);
                db.Configuration.Add(new PanelConfiguration { Key = "DeletingDomain:" + name,
                    ValueJson = JsonSerializer.Serialize(new { id = existing.Id, username = owner.Username }) });
                operations.Schedule("domain-cleanup", existing.Id, "delete", new { name, username = owner.Username, zoneIds = zones.Select(x => x.Id).ToArray() });
                db.Domains.Remove(existing);
                operations.Schedule("domain", existing.Id, "delete", new { name });
                await provisioning.ScheduleWebsiteAsync(existing, "delete", ct);
            }
            else if (input.Action == "modify")
            {
                var settings = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(existing.SettingsJson) ?? new();
                settings["php"] = JsonSerializer.SerializeToElement(input.Php);
                settings["ssl"] = JsonSerializer.SerializeToElement(input.Ssl);
                settings["cgi"] = JsonSerializer.SerializeToElement(input.Cgi);
                existing.SettingsJson = JsonSerializer.Serialize(settings);
                operations.Schedule("domain", existing.Id, "update", new { name });
                await provisioning.ScheduleWebsiteAsync(existing, "update", ct);
            }
            else return new(400, "INVALID_OPERATION");
        }
        await db.SaveChangesAsync(ct);
        certificateWork?.DomainChanged(changedId);
        notifications?.PublishChange(ReconciliationTarget.Website | ReconciliationTarget.Dns);
        return new(202, null);
    }

    public async Task<DomainDetailResult> GetDetailsAsync(string domain, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var principal = await identity.AuthenticateAsync(auth, ct);
        if (principal is null) return new(401, null, "UNAUTHORIZED");
        var ids = await identity.VisibleAccountsAsync(principal, ct);
        var row = await db.Domains.AsNoTracking().FirstOrDefaultAsync(x => x.Name == domain.ToLowerInvariant(), ct);
        if (row is null) return new(404, null, "DOMAIN_NOT_FOUND");
        if (!ids.Contains(row.OwnerId)) return new(403, null, "ACCESS_DENIED");
        var properties = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(row.SettingsJson) ?? new();
        var settings = properties.ToDictionary(x => x.Key, x => x.Value.ToString());
        settings["domain"] = row.Name;
        settings[MailDnsPolicy.PublicationKey] = MailDnsPolicy.Read(row.SettingsJson).Publish ? "ON" : "OFF";
        settings[MailDnsPolicy.RotationKey] = MailDnsPolicy.Read(row.SettingsJson).Rotate ? "ON" : "OFF";
        settings["username"] = await db.Accounts.Where(x => x.Id == row.OwnerId).Select(x => x.Username).SingleAsync(ct);
        settings["provisioning"] = (await ReadProvisioningAsync([row.Id], ct))[row.Id];
        return new(200, settings, null);
    }

    // All domain IDs originate from an authorized query. Fetch only related operations.
    private async Task<Dictionary<Guid, string>> ReadProvisioningAsync(Guid[] domainIds, CancellationToken ct)
    {
        if (domainIds.Length == 0) return new();
        var domainOps = await db.ReconciliationOperations.AsNoTracking()
            .Where(x => domainIds.Contains(x.ResourceId) &&
                (x.ResourceType == "website" || (x.ResourceType == "domain-dns" && x.Status == "pending")))
            .OrderBy(x => x.Id).ToListAsync(ct);
        var dnsOps = await (from zone in db.DnsZones.AsNoTracking()
                            join op in db.ReconciliationOperations.AsNoTracking() on zone.Id equals op.ResourceId
                            where zone.DomainId.HasValue && domainIds.Contains(zone.DomainId.Value) && op.ResourceType == "dns-zone"
                            select new { DomainId = zone.DomainId.Value, Operation = op }).ToListAsync(ct);
        var byDomain = domainOps.ToLookup(x => x.ResourceId);
        var byZone = dnsOps.ToLookup(x => x.DomainId, x => x.Operation);
        var result = new Dictionary<Guid, string>(domainIds.Length);
        foreach (var id in domainIds)
        {
            var ops = byDomain[id];
            var website = ops.Where(x => x.ResourceType == "website");
            var blocked = ops.FirstOrDefault(x => x.ResourceType == "domain-dns");
            var pending = website.Concat(byZone[id]).Where(x => x.Status == "pending").ToArray();
            result[id] = blocked is not null ? "DNS provisioning blocked: " + blocked.LastError :
                pending.Any(x => x.Attempts > 0) ? "Retrying" : pending.Length > 0 ? "Pending" :
                website.Any(x => x.Status == "completed") ? "Configuration applied" : "Unknown";
        }
        return result;
    }

    public async Task<DomainMutationResult> UpdateSettingAsync(string domain, string setting, string value,
        AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var principal = await identity.AuthenticateAsync(auth, ct);
        if (principal is null) return new(401, "UNAUTHORIZED");
        var ids = await identity.VisibleAccountsAsync(principal, ct);
        var row = await db.Domains.FirstOrDefaultAsync(x => x.Name == domain.ToLowerInvariant(), ct);
        if (row is null) return new(404, "DOMAIN_NOT_FOUND");
        if (!ids.Contains(row.OwnerId)) return new(403, "ACCESS_DENIED");
        if (setting is not ("php" or "ssl" or "cgi" or MailDnsPolicy.PublicationKey or MailDnsPolicy.RotationKey) || value is not ("ON" or "OFF"))
            return new(400, "INVALID_SETTING");
        var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(row.SettingsJson) ?? new();
        values[setting] = JsonSerializer.SerializeToElement(value == "ON");
        row.SettingsJson = JsonSerializer.Serialize(values);
        if (setting is MailDnsPolicy.PublicationKey or MailDnsPolicy.RotationKey)
        {
            if (await db.ReconciliationOperations.AnyAsync(x => x.ResourceType == "mail-dns" && x.ResourceId == row.Id, ct))
                operations.Schedule("mail-dns", row.Id, "update", new { row.Name });
            await db.SaveChangesAsync(ct);
            notifications?.PublishChange(ReconciliationTarget.Dns);
            return new(202, null);
        }
        operations.Schedule("domain", row.Id, "update", new { row.Name, setting });
        await provisioning.ScheduleWebsiteAsync(row, "update", ct);
        await db.SaveChangesAsync(ct);
        certificateWork?.DomainChanged(row.Id);
        notifications?.PublishChange(ReconciliationTarget.Website | ReconciliationTarget.Dns);
        return new(202, null);
    }
    public IResult PreviewDomain(DomainCreatePreview input)
    {
        if (!KubernetesInputValidation.ValidDomain(input.Domain))
            return Results.Json(new { type = "INVALID_DOMAIN" }, statusCode: 400);
        return Results.Json(new {
            domain = input.Domain.ToLowerInvariant(), dryRun = true,
            desiredSettings = new { php = input.Php, ssl = input.Ssl, cgi = input.Cgi },
            executed = false
        });
    }
}
