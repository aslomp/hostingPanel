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

public sealed class KubernetesProvisioningService(HostingPanelDbContext db, KubernetesSettingsService settingsProvider, KubernetesOperationQueue operations, PowerDnsClient? powerDns = null) : IDomainDnsStaging
{
    public async Task ScheduleWebsiteAsync(HostedDomain domain, string action, CancellationToken ct)
    {
        var settings = await settingsProvider.GetEffectiveSettingsAsync(ct);
        using var config = JsonDocument.Parse(domain.SettingsJson);
        operations.Schedule("website", domain.Id, action, new
        {
            domain = domain.Name,
            ownerId = domain.OwnerId,
            configuration = config.RootElement.Clone(),
            ingressProvider = settings.IngressProvider.ToString()
        });
    }

    // Stage DNS and website desired state in the same SaveChanges transaction.
    public async Task<string?> StageDomainDnsAsync(HostedDomain domain, CancellationToken ct, bool retry = false)
    {
        if (powerDns is null) return null;
        var dnsSettings = await settingsProvider.GetEffectiveSettingsAsync(ct);
        if (!dnsSettings.DnsAutoCreateZones && !retry) return null;
        var existing = await db.DnsZones.SingleOrDefaultAsync(z => z.Name == domain.Name, ct);
        if (existing is not null)
        {
            if (existing.OwnerId != domain.OwnerId || (existing.DomainId is not null && existing.DomainId != domain.Id))
                return "DNS_ZONE_EXISTS";
            existing.DomainId = domain.Id;
            operations.Schedule("dns-zone", existing.Id, "update", new { existing.Name });
            return null;
        }
        var owner = db.Accounts.Local.FirstOrDefault(x => x.Id == domain.OwnerId)
            ?? await db.Accounts.AsNoTracking().SingleAsync(x => x.Id == domain.OwnerId, ct);
        var parent = owner.CreatorId is { } creator
            ? db.Accounts.Local.FirstOrDefault(x => x.Id == creator) ?? await db.Accounts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == creator, ct) : null;
        var direct = dnsSettings.IpAddresses.Where(x => x.Status is "server" or "shared" or "owned" &&
            (x.AssignedUsers ?? []).Contains(owner.Username)).ToArray();
        var reseller = dnsSettings.IpAddresses.Where(x => x.Status == "shared" &&
            ((x.Resellers ?? []).Contains(owner.Username) || (parent is not null && (x.Resellers ?? []).Contains(parent.Username)))).ToArray();
        var global = dnsSettings.IpAddresses.Where(x => x.Status == "shared" && x.Global).ToArray();
        var server = dnsSettings.IpAddresses.Where(x => x.Status == "server").ToArray();
        var eligible = direct.Length > 0 ? direct : reseller.Length > 0 ? reseller : global.Length > 0 ? global : server;
        var selected = dnsSettings.IpAddresses.Length == 0 ? new[] { dnsSettings.DnsWebIp } :
            direct.Length > 0 ? direct.Select(x => x.Address).ToArray() :
            eligible.OrderByDescending(x => x.Address == dnsSettings.DnsWebIp).ThenBy(x => x.Address, StringComparer.Ordinal)
                .Take(1).Select(x => x.Address).ToArray();
        if (selected.Length == 0) return "NO_ASSIGNED_IP";
        var nameservers = await KubernetesAccountNameservers.ResolveAsync(db, owner, dnsSettings, ct);
        var ns1 = nameservers.ElementAtOrDefault(0) ?? "";
        var ns2 = nameservers.ElementAtOrDefault(1) ?? "";
        if (selected.Any(x => !System.Net.IPAddress.TryParse(x, out _)) ||
            !KubernetesInputValidation.ValidDomain(ns1) || !KubernetesInputValidation.ValidDomain(ns2) || string.Equals(ns1, ns2, StringComparison.OrdinalIgnoreCase))
            return "DNS_DEFAULTS_NOT_CONFIGURED";
        var zone = new DnsZone { Name = domain.Name, DomainId = domain.Id, OwnerId = domain.OwnerId };
        db.DnsZones.Add(zone);
        foreach (var address in selected.Select(System.Net.IPAddress.Parse).Distinct())
            db.DnsRecords.Add(new DnsRecordEntity { ZoneId = zone.Id, Name = "@", Type = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? "A" : "AAAA", Value = address.ToString() });
        db.DnsRecords.Add(new DnsRecordEntity { ZoneId = zone.Id, Name = "www", Type = "CNAME", Value = domain.Name + "." });
        foreach (var ns in new[] { ns1!, ns2! })
            db.DnsRecords.Add(new DnsRecordEntity { ZoneId = zone.Id, Name = "@", Type = "NS", Value = ns.ToLowerInvariant() + "." });
        operations.Schedule("dns-zone", zone.Id, "create", new { zone.Name });
        return null;
    }
}
