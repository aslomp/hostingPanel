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

public sealed class KubernetesDnsService(HostingPanelDbContext db, KubernetesIdentityService identity, KubernetesOperationQueue operations, IConfiguration? configuration = null, PowerDnsClient? powerDns = null) : IDNSProvider
{
    public async Task<AccessibleDnsInventory> ListAccessibleZonesAsync(AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var principal = await identity.AuthenticateAsync(auth, ct);
        if (principal is null) return new(401, null, "UNAUTHORIZED");
        var ids = await identity.VisibleAccountsAsync(principal, ct);
        var zones = await (from z in db.DnsZones.AsNoTracking()
            join a in db.Accounts.AsNoTracking() on z.OwnerId equals a.Id
            where ids.Contains(z.OwnerId)
            orderby z.Name
            select new AccessibleDnsZone(z.Name, null, null, a.Username)).ToListAsync(ct);
        return new(200, zones, null);
    }

    public async Task<AdminDnsZoneListResult> ListAdminZonesAsync(AuthenticationHeaderValue auth, int page, CancellationToken ct)
    {
        var principal = await identity.AuthenticateAsync(auth, ct);
        if (principal is null) return new(401, null, "UNAUTHORIZED");
        if (page < 1 || page > 10000) return new(400, null, "INVALID_PAGE");
        var ids = await identity.VisibleAccountsAsync(principal, ct);
        var zones = await db.DnsZones.AsNoTracking().Where(z => ids.Contains(z.OwnerId))
            .OrderBy(z => z.Name).Skip((page - 1) * 100).Take(100)
            .Select(z => new AdminDnsZone(z.Name, null, null)).ToListAsync(ct);
        return new(200, zones, null);
    }

    public async Task<DnsZoneResult> ReadAsync(string domain, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var principal = await identity.AuthenticateAsync(auth, ct);
        if (principal is null) return new(401, null, "UNAUTHORIZED");
        var ids = await identity.VisibleAccountsAsync(principal, ct);
        var zone = await db.DnsZones.AsNoTracking().FirstOrDefaultAsync(z => z.Name == domain.ToLowerInvariant(), ct);
        if (zone is null) return new(404, null, "ZONE_NOT_FOUND");
        if (!ids.Contains(zone.OwnerId)) return new(403, null, "ACCESS_DENIED");
        var records = await db.DnsRecords.AsNoTracking().Where(x => x.ZoneId == zone.Id)
            .OrderBy(x => x.Name).ThenBy(x => x.Type).ToListAsync(ct);
        var result = records.Select(r => new DnsRecord(r.Type, r.Name, r.Value, r.Ttl.ToString())).ToList();
        if (await DelegatedAsync(zone, ct))
        {
            if (powerDns is null) return new(503, null, "DKIM_DNS_UNAVAILABLE");
            try
            {
                var current = await powerDns.ReadDkimAsync(zone.Name, ct);
                result.RemoveAll(r => StalwartDnsProvisioning.IsDkim(zone.Name, r.Name, r.Type));
                result.AddRange(current);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or InvalidOperationException or JsonException or OperationCanceledException or KeyNotFoundException)
            { return new(503, null, "DKIM_DNS_UNAVAILABLE"); }
        }
        return new(200, result.OrderBy(x => x.Name).ThenBy(x => x.Type).ToArray(), null);
    }

    private Task<bool> DelegatedAsync(DnsZone zone, CancellationToken ct) =>
        configuration is not null && StalwartDnsProviderRegistration.Enabled(configuration) && zone.DomainId.HasValue
            ? db.ReconciliationOperations.AnyAsync(x => x.ResourceType == "mail-dns" && x.ResourceId == zone.DomainId.Value, ct)
            : Task.FromResult(false);

    public async Task<DnsChangeResult> ChangeAsync(string domain, DnsChange change, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var principal = await identity.AuthenticateAsync(auth, ct);
        if (principal is null) return new(401, "UNAUTHORIZED");
        if (!KubernetesInputValidation.ValidDomain(domain) || !DnsRecordValidation.Valid(change.Record)
            || change.Action is not ("add" or "edit" or "delete")
            || (change.Action != "add" && (change.Previous is null || !DnsRecordValidation.Valid(change.Previous))))
            return new(400, "INVALID_DNS_CHANGE");
        var ids = await identity.VisibleAccountsAsync(principal, ct);
        var zone = await db.DnsZones.FirstOrDefaultAsync(x => x.Name == domain.ToLowerInvariant(), ct);
        if (zone is null) return new(404, "ZONE_NOT_FOUND");
        if (!ids.Contains(zone.OwnerId)) return new(403, "ACCESS_DENIED");
        if (await DelegatedAsync(zone, ct) &&
            (StalwartDnsProvisioning.IsDkim(zone.Name, change.Record.Name, change.Record.Type)
             || change.Previous is not null && StalwartDnsProvisioning.IsDkim(zone.Name, change.Previous.Name, change.Previous.Type)))
            return new(409, "DKIM_MANAGED_BY_STALWART");
        var prev = change.Action == "add" ? null : change.Previous;
        var previous = prev is null ? null : await db.DnsRecords.FirstOrDefaultAsync(x =>
            x.ZoneId == zone.Id && x.Name == prev.Name && x.Type == prev.Type && x.Value == prev.Value, ct);
        if (change.Action != "add" && previous is null) return new(404, "DNS_RECORD_NOT_FOUND");
        if (previous is not null) db.DnsRecords.Remove(previous);
        if (change.Action != "delete") db.DnsRecords.Add(new DnsRecordEntity {
            ZoneId = zone.Id, Name = change.Record.Name, Type = change.Record.Type,
            Value = change.Record.Value, Ttl = int.TryParse(change.Record.Ttl, out var ttl) ? ttl : zone.DefaultTtl
        });
        operations.Schedule("dns-zone", zone.Id, "update", new { zone.Name });
        await db.SaveChangesAsync(ct);
        return new(202, null);
    }

    public async Task<DnsChangeResult> CreateZoneAsync(DnsZoneCreate input, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var principal = await identity.AuthenticateAsync(auth, ct);
        if (principal is null) return new(401, "UNAUTHORIZED");
        if (!KubernetesInputValidation.ValidDomain(input.Domain) || !KubernetesInputValidation.ValidDomain(input.Ns1) || !KubernetesInputValidation.ValidDomain(input.Ns2)
            || !System.Net.IPAddress.TryParse(input.Ip, out var ip)
            || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return new(400, "INVALID_DNS_ZONE");
        var name = input.Domain.ToLowerInvariant();
        var domain = await db.Domains.FirstOrDefaultAsync(d => d.Name == name, ct);
        var ids = await identity.VisibleAccountsAsync(principal, ct);
        if (domain is not null && !ids.Contains(domain.OwnerId)) return new(403, "ACCESS_DENIED");
        if (domain is null && principal.Kind != "admin") return new(403, "ACCESS_DENIED");
        if (await db.DnsZones.AnyAsync(z => z.Name == name, ct)) return new(409, "ZONE_EXISTS");
        var zone = new DnsZone { Name = name, DomainId = domain?.Id, OwnerId = domain?.OwnerId ?? principal.Id };
        db.DnsZones.Add(zone);
        db.DnsRecords.Add(new DnsRecordEntity { ZoneId = zone.Id, Name = "@", Type = "A", Value = ip.ToString() });
        foreach (var ns in new[] { input.Ns1, input.Ns2 }.Select(x => x.TrimEnd('.').ToLowerInvariant()).Distinct())
            db.DnsRecords.Add(new DnsRecordEntity { ZoneId = zone.Id, Name = "@", Type = "NS", Value = ns + "." });
        operations.Schedule("dns-zone", zone.Id, "create", new { zone.Name });
        await db.SaveChangesAsync(ct);
        return new(202, null);
    }

    public async Task<DnsChangeResult> DeleteZoneAsync(string domain, string confirmation, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var principal = await identity.AuthenticateAsync(auth, ct);
        if (principal is null) return new(401, "UNAUTHORIZED");
        if (!KubernetesInputValidation.ValidDomain(domain) || domain != confirmation) return new(400, "CONFIRMATION_REQUIRED");
        var zone = await db.DnsZones.FirstOrDefaultAsync(z => z.Name == domain.ToLowerInvariant(), ct);
        if (zone is null) return new(404, "ZONE_NOT_FOUND");
        var ids = await identity.VisibleAccountsAsync(principal, ct);
        if (!ids.Contains(zone.OwnerId)) return new(403, "ACCESS_DENIED");
        db.DnsZones.Remove(zone);
        operations.Schedule("dns-zone", zone.Id, "delete", new { zone.Name });
        await db.SaveChangesAsync(ct);
        return new(202, null);
    }
}
