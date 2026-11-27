using System.Net.Http.Headers;
using System.Text.Json;
using HostingPanel.Api.Persistence;
using HostingPanel.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace HostingPanel.Api;

internal sealed class StalwartDnsProvisioning(HostingPanelDbContext db, IHttpClientFactory clients,
    IConfiguration configuration, KubernetesOperationQueue operations)
{
    internal async Task<string?> StageAsync(Guid domainId, CancellationToken ct)
    {
        if (!configuration.GetValue<bool>("Stalwart:Enabled") || !configuration.GetValue("Stalwart:AutoProvisionDns", true))
            return "MAIL_DNS_DISABLED";
        var domain = await db.Domains.AsNoTracking().SingleOrDefaultAsync(d => d.Id == domainId, ct);
        if (domain is null) return null;
        if (domain.Suspended) return "MAIL_DOMAIN_SUSPENDED";
        var endpoint = configuration["Stalwart:BaseUrl"];
        var token = configuration["Stalwart:ApiKey"];
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || string.IsNullOrWhiteSpace(token)) return "MAIL_NOT_CONFIGURED";
        using var client = clients.CreateClient("stalwart-management");
        client.BaseAddress = new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var matches = (await StalwartMailProvider.Query(client, "Domain", new { name = domain.Name },
            new[] { "id", "name", "dnsZoneFile" }, ct))
            .Where(x => x.GetProperty("name").GetString() == domain.Name).ToArray();
        if (matches.Length != 1 || !matches[0].TryGetProperty("dnsZoneFile", out var file)
            || file.ValueKind != JsonValueKind.String) return "MAIL_DNS_NOT_READY";
        var desired = StalwartDnsRecords.Parse(domain.Name, file.GetString()!);
        // No network requests inside the database transaction.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var current = await db.Domains.AsNoTracking().SingleOrDefaultAsync(d => d.Id == domainId, ct);
        if (current is null) return null;
        if (current.OwnerId != domain.OwnerId || current.Name != domain.Name || current.Suspended)
            return "MAIL_DOMAIN_CHANGED";
        var zone = await db.DnsZones.SingleOrDefaultAsync(z => z.Name == domain.Name, ct);
        if (zone is null) return "MAIL_DNS_ZONE_NOT_READY";
        if (zone.OwnerId != domain.OwnerId || zone.DomainId != domain.Id) return "MAIL_DNS_ZONE_OWNERSHIP_CONFLICT";
        var existing = await db.DnsRecords.Where(x => x.ZoneId == zone.Id).ToListAsync(ct);
        var additions = Plan(domain.Name, existing, desired);
        if (additions is null) return "MAIL_DNS_RECORD_CONFLICT";
        foreach (var record in additions)
            db.DnsRecords.Add(new DnsRecordEntity { ZoneId = zone.Id, Name = record.Name, Type = record.Type,
                Value = record.Value, Ttl = int.Parse(record.Ttl!) });
        // Even if already staged, ensure an authoritative DNS apply is queued.
        if (!await db.ReconciliationOperations.AnyAsync(x => x.ResourceType == "dns-zone" && x.ResourceId == zone.Id && x.Status == "pending", ct))
            operations.Schedule("dns-zone", zone.Id, "update", new { zone.Name });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return null;
    }

    internal static IReadOnlyList<DnsRecord>? Plan(string domain, IReadOnlyList<DnsRecordEntity> existing, IReadOnlyList<DnsRecord> desired)
    {
        string Owner(string name)
        {
            var zone = domain.TrimEnd('.').ToLowerInvariant();
            var value = name.ToLowerInvariant();
            if (value == "@") return zone + ".";
            if (value.EndsWith('.')) return value;
            return value == zone || value.EndsWith("." + zone) ? value + "." : value + "." + zone + ".";
        }
        static string Content(string value) => value.Replace("\" \"", "").Trim('"');
        static bool Spf(string value) => Content(value).StartsWith("v=spf1", StringComparison.OrdinalIgnoreCase);
        var additions = new List<DnsRecord>();
        foreach (var group in desired.GroupBy(x => (Owner(x.Name), x.Type)))
        {
            var atOwner = existing.Where(x => Owner(x.Name) == group.Key.Item1).ToArray();
            if (atOwner.Any(x => x.Type.Equals("CNAME", StringComparison.OrdinalIgnoreCase))) return null;
            var same = atOwner.Where(x => x.Type.Equals(group.Key.Type, StringComparison.OrdinalIgnoreCase)
                && (group.Key.Type != "TXT" || !group.Any(d => Spf(d.Value)) || Spf(x.Value))).ToArray();
            string Normalize(string value) => group.Key.Type == "TXT" ? Content(value) : value.TrimEnd('.').ToLowerInvariant();
            var expected = group.Select(x => Normalize(x.Value)).ToHashSet(StringComparer.Ordinal);
            if (same.Any(x => !expected.Contains(Normalize(x.Value)))) return null;
            additions.AddRange(group.Where(x => !same.Any(y => Normalize(y.Value) == Normalize(x.Value))));
        }
        return additions;
    }
}
