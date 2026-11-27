using HostingPanel.Api.Adapters;
using HostingPanel.Providers.PowerDNS;
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
            new[] { "id", "name", "dnsZoneFile", "dnsManagement", "dkimManagement" }, ct))
            .Where(x => x.GetProperty("name").GetString() == domain.Name).ToArray();
        if (matches.Length != 1) return "MAIL_DNS_NOT_READY";
        var nativeRotation = StalwartDnsProviderRegistration.Enabled(configuration);
        IReadOnlyList<DnsRecord> desired = [];
        if (!nativeRotation)
        {
            if (!matches[0].TryGetProperty("dnsZoneFile", out var file) || file.ValueKind != JsonValueKind.String)
                return "MAIL_DNS_NOT_READY";
            desired = StalwartDnsRecords.Parse(domain.Name, file.GetString()!);
        }
        var dnsServerId = nativeRotation ? await StalwartDnsProviderRegistration.EnsureAsync(client, configuration, ct) : null;
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
        // Bootstrap MX/SPF/DMARC once, without replacing existing customer records.
        // Subsequent mailbox creation must not recreate deliberately deleted records.
        if (nativeRotation && !await db.ReconciliationOperations.AnyAsync(x =>
            x.ResourceType == "mail-dns" && x.ResourceId == domain.Id && x.Status == "completed", ct))
        {
            if (!matches[0].TryGetProperty("dnsZoneFile", out var initialFile) || initialFile.ValueKind != JsonValueKind.String)
                return "MAIL_DNS_NOT_READY";
            desired = StalwartDnsRecords.Parse(domain.Name, initialFile.GetString()!)
                .Where(x => !IsDkim(domain.Name, x.Name, x.Type))
                .Where(x => !existing.Any(e => e.Type.Equals(x.Type, StringComparison.OrdinalIgnoreCase) &&
                    (x.Type == "MX" || e.Value.TrimStart('"').StartsWith("v=spf1", StringComparison.OrdinalIgnoreCase) && x.Value.TrimStart('"').StartsWith("v=spf1", StringComparison.OrdinalIgnoreCase) ||
                    e.Value.TrimStart('"').StartsWith("v=DMARC1", StringComparison.OrdinalIgnoreCase) && x.Value.TrimStart('"').StartsWith("v=DMARC1", StringComparison.OrdinalIgnoreCase))))
                .ToArray();
        }
        var additions = Plan(domain.Name, existing, desired);
        if (additions is null) return "MAIL_DNS_RECORD_CONFLICT";
        foreach (var record in additions)
            db.DnsRecords.Add(new DnsRecordEntity { ZoneId = zone.Id, Name = record.Name, Type = record.Type,
                Value = record.Value, Ttl = int.Parse(record.Ttl!) });
        // Manual imports need a publication operation; native mail DNS waits for the existing zone publication.
        if ((!nativeRotation || additions.Count > 0) && !await db.ReconciliationOperations.AnyAsync(x => x.ResourceType == "dns-zone" && x.ResourceId == zone.Id && x.Status == "pending", ct))
            operations.Schedule("dns-zone", zone.Id, "update", new { zone.Name });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        if (nativeRotation)
        {
            // Publish the zone before Stalwart enables automatic management.
            // A pending dns-zone operation is processed later in this same pass.
            if (await db.ReconciliationOperations.AnyAsync(x => x.ResourceType == "dns-zone" &&
                    x.ResourceId == zone.Id && x.Status == "pending", ct))
                return "MAIL_DNS_PUBLICATION_PENDING";
            // The DNS reconciler must preserve delegated mail records before this is enabled.
            // Never replace an unrelated automatic DNS configuration.
            var upstream = matches[0];
            var management = upstream.GetProperty("dnsManagement");
            var mode = management.GetProperty("@type").GetString();
            if (mode == "Automatic")
            {
                if (management.GetProperty("dnsServerId").GetString() != dnsServerId
                    || (management.TryGetProperty("origin", out var origin) && origin.GetString() != domain.Name)
                    || !management.TryGetProperty("publishRecords", out var published)
                    || published.EnumerateObject().Any(x => x.Name is not ("mx" or "spf" or "dkim" or "dmarc") && x.Value.ValueKind == JsonValueKind.True))
                    return "MAIL_DNS_MANAGEMENT_CONFLICT";
            }
            else if (mode != "Manual") return "MAIL_DNS_MANAGEMENT_CONFLICT";
            var signing = upstream.GetProperty("dkimManagement");
            var automatic = signing.GetProperty("@type").GetString() == "Automatic";
            var patch = new Dictionary<string, object>();
            if (mode == "Manual" || MailDnsOwnership.PublishRecords.Any(key => !management.GetProperty("publishRecords").TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.True) || management.GetProperty("publishRecords").EnumerateObject().Any(x => !MailDnsOwnership.PublishRecords.Contains(x.Name) && x.Value.ValueKind == JsonValueKind.True)) patch["dnsManagement"] = new Dictionary<string, object> {
                ["@type"] = "Automatic", ["dnsServerId"] = dnsServerId!, ["origin"] = domain.Name,
                ["publishRecords"] = new Dictionary<string, bool> { ["dkim"] = true, ["mx"] = false, ["spf"] = false, ["dmarc"] = false }
            };
            if (!automatic) patch["dkimManagement"] = new Dictionary<string, object> { ["@type"] = "Automatic" };
            if (patch.Count > 0)
            {
                var id = upstream.GetProperty("id").GetString()!;
                var result = await StalwartMailProvider.Call(client, "x:Domain/set", new {
                    update = new Dictionary<string, object> { [id] = patch }
                }, ct);
                if (!result.TryGetProperty("updated", out var updated) || updated.ValueKind != JsonValueKind.Object
                    || !updated.TryGetProperty(id, out _)) return "MAIL_DKIM_ENABLE_FAILED";
            }
        }
        return null;
    }

    internal static bool IsDkim(string domain, string name, string type)
    {
        if (!type.Equals("TXT", StringComparison.OrdinalIgnoreCase)) return false;
        var zone = domain.TrimEnd('.').ToLowerInvariant();
        var owner = name.TrimEnd('.').ToLowerInvariant();
        if (!owner.EndsWith("." + zone)) owner += "." + zone;
        return owner.EndsWith("._domainkey." + zone, StringComparison.Ordinal);
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
