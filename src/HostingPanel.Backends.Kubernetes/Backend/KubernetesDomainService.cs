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

public sealed class KubernetesDomainService(HostingPanelDbContext db, KubernetesIdentityService identity, KubernetesProvisioningService provisioning, KubernetesOperationQueue operations) : IDomains, IAccessibleDomains, IDomainPreview
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
                    select new VisibleDomain(domain.Name, owner.Username, owner.Kind);
        return new(200, await query.ToListAsync(ct), null);
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
                    select domain.Name;
        return new(200, await query.OrderBy(x => x).ToListAsync(ct), null);
    }
    public async Task<DomainMutationResult> MutateAsync(DomainMutationRequest input, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var principal = await identity.AuthenticateAsync(auth, ct);
        if (principal is null) return new(401, "UNAUTHORIZED");
        if (!KubernetesInputValidation.ValidDomain(input.Domain) || input.Confirmation != input.Domain) return new(400, "CONFIRMATION_REQUIRED");
        var name = input.Domain.ToLowerInvariant();
        var existing = await db.Domains.FirstOrDefaultAsync(x => x.Name == name, ct);
        if (input.Action == "create")
        {
            if (await db.Configuration.AnyAsync(x => x.Key == "DeletingDomain:" + name, ct)) return new(409, "DOMAIN_DELETION_PENDING");
            if (existing is not null) return new(409, "DOMAIN_EXISTS");
            var created = new HostedDomain { OwnerId = principal.Id, Name = name,
                SettingsJson = JsonSerializer.Serialize(new { php = input.Php, ssl = input.Ssl, cgi = input.Cgi }) };
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
                existing.SettingsJson = JsonSerializer.Serialize(new { php = input.Php, ssl = input.Ssl, cgi = input.Cgi });
                operations.Schedule("domain", existing.Id, "update", new { name });
                await provisioning.ScheduleWebsiteAsync(existing, "update", ct);
            }
            else return new(400, "INVALID_OPERATION");
        }
        await db.SaveChangesAsync(ct);
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
        settings["username"] = await db.Accounts.Where(x => x.Id == row.OwnerId).Select(x => x.Username).SingleAsync(ct);
        var operations = await db.ReconciliationOperations.AsNoTracking().Where(x => x.ResourceType == "website" && x.ResourceId == row.Id).ToListAsync(ct);
        var dnsStage = await db.ReconciliationOperations.AsNoTracking().Where(x => x.ResourceType == "domain-dns" && x.ResourceId == row.Id && x.Status == "pending").ToListAsync(ct);
        var zoneIds = await db.DnsZones.Where(x => x.DomainId == row.Id).Select(x => x.Id).ToListAsync(ct);
        var dnsOps = await db.ReconciliationOperations.AsNoTracking().Where(x => x.ResourceType == "dns-zone" && zoneIds.Contains(x.ResourceId)).ToListAsync(ct);
        var pending = operations.Concat(dnsOps).Where(x => x.Status == "pending").ToArray();
        var blocked = dnsStage.FirstOrDefault();
        settings["provisioning"] = blocked is not null ? "DNS provisioning blocked: " + blocked.LastError :
            pending.Any(x => x.Attempts > 0) ? "Retrying" : pending.Length > 0 ? "Pending" :
            operations.Any(x => x.Status == "completed") ? "Configuration applied" : "Unknown";
        return new(200, settings, null);
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
        if (setting is not ("php" or "ssl" or "cgi") || value is not ("ON" or "OFF"))
            return new(400, "INVALID_SETTING");
        var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(row.SettingsJson) ?? new();
        values[setting] = JsonSerializer.SerializeToElement(value == "ON");
        row.SettingsJson = JsonSerializer.Serialize(values);
        operations.Schedule("domain", row.Id, "update", new { row.Name, setting });
        await provisioning.ScheduleWebsiteAsync(row, "update", ct);
        await db.SaveChangesAsync(ct);
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
