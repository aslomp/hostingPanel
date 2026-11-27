using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HostingPanel.Api;
using HostingPanel.Api.Adapters;
using HostingPanel.Api.Services;

namespace HostingPanel.Providers.PowerDNS;

// The supplied desired-state provider owns authentication, authorization and durable scheduling.
// Provisioning methods are trusted worker operations, never exposed as HTTP endpoints.
public sealed class PowerDnsProvider(IDNSProvider desiredState, PowerDnsClient client) : IDNSProvider
{
    public Task<AccessibleDnsInventory> ListAccessibleZonesAsync(AuthenticationHeaderValue auth, CancellationToken ct) => desiredState.ListAccessibleZonesAsync(auth, ct);
    public Task<AdminDnsZoneListResult> ListAdminZonesAsync(AuthenticationHeaderValue auth, int page, CancellationToken ct) => desiredState.ListAdminZonesAsync(auth, page, ct);
    public Task<DnsZoneResult> ReadAsync(string domain, AuthenticationHeaderValue auth, CancellationToken ct) => desiredState.ReadAsync(domain, auth, ct);
    public Task<DnsChangeResult> ChangeAsync(string domain, DnsChange change, AuthenticationHeaderValue auth, CancellationToken ct) => desiredState.ChangeAsync(domain, change, auth, ct);
    public Task<DnsChangeResult> CreateZoneAsync(DnsZoneCreate input, AuthenticationHeaderValue auth, CancellationToken ct) => desiredState.CreateZoneAsync(input, auth, ct);
    public Task<DnsChangeResult> DeleteZoneAsync(string domain, string confirmation, AuthenticationHeaderValue auth, CancellationToken ct) => desiredState.DeleteZoneAsync(domain, confirmation, auth, ct);
    public Task ProvisionAsync(string zone, IReadOnlyList<DnsRecord> records, CancellationToken ct, bool preserveDkim = false) => client.ApplyAsync(zone, records, ct, preserveDkim);
    public Task DeprovisionAsync(string zone, CancellationToken ct) => client.DeleteAsync(zone, ct);
}

public sealed class PowerDnsClient(HttpClient http)
{
    private static string Fqdn(string name) => name.TrimEnd('.').ToLowerInvariant() + ".";
    private static string Owner(string zone, string name)
    {
        var result = name == "@" ? zone : name.EndsWith('.') ? Fqdn(name) :
            name.Equals(zone.TrimEnd('.'), StringComparison.OrdinalIgnoreCase) || name.EndsWith("." + zone.TrimEnd('.'), StringComparison.OrdinalIgnoreCase)
                ? Fqdn(name) : Fqdn(name + "." + zone);
        if (result != zone && !result.EndsWith("." + zone, StringComparison.Ordinal))
            throw new InvalidOperationException("DNS owner outside zone.");
        return result;
    }
    private static string Content(DnsRecord r) => r.Type switch
    {
        "NS" or "CNAME" or "PTR" => Fqdn(r.Value),
        "TXT" when !r.Value.StartsWith('"') => "\"" + r.Value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"",
        "MX" or "SRV" => NormalizeTarget(r.Value),
        _ => r.Value
    };
    private static string NormalizeTarget(string value)
    {
        var i = value.LastIndexOf(' ');
        return i < 0 ? value : value[..(i + 1)] + (value[(i + 1)..] == "." ? "." : Fqdn(value[(i + 1)..]));
    }
    private static string PathFor(string zone) => "zones/" + Uri.EscapeDataString(Fqdn(zone));
    private async Task<JsonDocument?> ReadAsync(string zone, CancellationToken ct)
    {
        using var response = await http.GetAsync(PathFor(zone), ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }
    public Task<IReadOnlyList<DnsRecord>> ReadDkimAsync(string domain, CancellationToken ct) => ReadMailAsync(domain, ct, false);
    public Task<IReadOnlyList<DnsRecord>> ReadManagedMailAsync(string domain, CancellationToken ct) => ReadMailAsync(domain, ct, true);

    private async Task<IReadOnlyList<DnsRecord>> ReadMailAsync(string domain, CancellationToken ct, bool all)
    {
        var zone = Fqdn(domain);
        using var actual = await ReadAsync(zone, ct) ?? throw new InvalidOperationException("DNS zone unavailable.");
        return actual.RootElement.GetProperty("rrsets").EnumerateArray()
            .Where(x => all ? MailDnsOwnership.Reserved(zone, x.GetProperty("name").GetString()!, x.GetProperty("type").GetString()!) :
                x.GetProperty("type").GetString() == "TXT" && x.GetProperty("name").GetString()!.EndsWith("._domainkey." + zone, StringComparison.OrdinalIgnoreCase))
            .SelectMany(x => x.GetProperty("records").EnumerateArray()
                .Where(r => !r.GetProperty("disabled").GetBoolean())
                .Select(r => new DnsRecord(x.GetProperty("type").GetString()!, x.GetProperty("name").GetString()!,
                    r.GetProperty("content").GetString()!, x.GetProperty("ttl").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture))))
            .ToArray();
    }
    public static bool SameRecord(string domain, DnsRecord a, DnsRecord b) =>
        Owner(Fqdn(domain), a.Name) == Owner(Fqdn(domain), b.Name) &&
        a.Type.Equals(b.Type, StringComparison.OrdinalIgnoreCase) && Content(a) == Content(b);

    // Explicit user edits operate against the current authoritative RRset.
    // Other TXT values are retained, including those published by Stalwart.
    public async Task ChangeTxtAsync(string domain, DnsChange change, CancellationToken ct)
    {
        if (change.Action is not ("add" or "edit" or "delete") ||
            !change.Record.Type.Equals("TXT", StringComparison.OrdinalIgnoreCase) ||
            (change.Action != "add" && (change.Previous is null ||
                !change.Previous.Type.Equals("TXT", StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException("Invalid shared TXT change.");
        var zone = Fqdn(domain);
        using var actual = await ReadAsync(zone, ct) ?? throw new InvalidOperationException("DNS zone unavailable.");
        var newOwner = Owner(zone, change.Record.Name);
        var oldOwner = change.Previous is null ? newOwner : Owner(zone, change.Previous.Name);
        var changes = new List<Rrset>();
        foreach (var owner in new[] { oldOwner, newOwner }.Distinct(StringComparer.Ordinal))
        {
            var current = actual.RootElement.GetProperty("rrsets").EnumerateArray().FirstOrDefault(x =>
                x.GetProperty("type").GetString() == "TXT" && x.GetProperty("name").GetString() == owner);
            var exists = current.ValueKind != JsonValueKind.Undefined;
            var old = exists ? current.GetProperty("records").EnumerateArray()
                .Select(r => new Entry(r.GetProperty("content").GetString()!, r.GetProperty("disabled").GetBoolean())).ToList() : new List<Entry>();
            var next = old.ToList();
            if (change.Action != "add" && owner == oldOwner)
                next.RemoveAll(x => x.content == Content(change.Previous!));
            if (change.Action != "delete" && owner == newOwner)
            {
                var content = Content(change.Record);
                if (!next.Any(x => x.content == content && !x.disabled)) next.Add(new Entry(content, false));
            }
            var ttl = exists ? current.GetProperty("ttl").GetInt32() :
                int.TryParse(change.Record.Ttl, out var requested) ? requested : 3600;
            if (owner == newOwner && change.Action != "delete" && int.TryParse(change.Record.Ttl, out var changedTtl)) ttl = changedTtl;
            if (old.SequenceEqual(next) && (!exists || ttl == current.GetProperty("ttl").GetInt32())) continue;
            changes.Add(new(owner, "TXT", ttl, next.Count == 0 ? "DELETE" : "REPLACE", next.ToArray()));
        }
        if (changes.Count == 0) return;
        using var patch = await http.PatchAsJsonAsync(PathFor(zone), new { rrsets = changes }, ct);
        patch.EnsureSuccessStatusCode();
    }

    private sealed record Entry(string content, bool disabled);
    private sealed record Rrset(string name, string type, int ttl, string changetype, Entry[] records);
    public async Task ApplyAsync(string domain, IReadOnlyList<DnsRecord> records, CancellationToken ct, bool preserveDkim = false)
    {
        var zone = Fqdn(domain);
        var desired = records.GroupBy(r => (name: Owner(zone, r.Name), type: r.Type.ToUpperInvariant()))
            .ToDictionary(g => g.Key, g => new Rrset(g.Key.name, g.Key.type,
                g.Select(r => int.TryParse(r.Ttl, out var ttl) ? ttl : 3600).Min(), "REPLACE",
                g.Select(r => new Entry(Content(r), false)).Distinct().OrderBy(r => r.content, StringComparer.Ordinal).ToArray()));
        bool ExternallyManaged(string name, string type) => preserveDkim
            && MailDnsOwnership.Reserved(zone, name, type);
        foreach (var key in desired.Keys.Where(k => ExternallyManaged(k.name, k.type) && k.type != "TXT").ToArray())
            desired.Remove(key);
        var ns = desired.GetValueOrDefault((zone, "NS"))?.records.Select(r => r.content).ToArray() ?? [];
        if (ns.Length == 0) throw new InvalidOperationException("A zone must retain at least one apex NS record.");
        using var initial = await ReadAsync(zone, ct);
        if (initial is null)
        {
            using var created = await http.PostAsJsonAsync("zones", new { name = zone, kind = "Native", nameservers = ns, soa_edit_api = "INCREASE" }, ct);
            if (created.StatusCode != HttpStatusCode.Conflict) created.EnsureSuccessStatusCode();
        }
        using var actual = await ReadAsync(zone, ct) ?? throw new InvalidOperationException("PowerDNS zone unavailable after creation.");
        var changes = new List<Rrset>();
        foreach (var current in actual.RootElement.GetProperty("rrsets").EnumerateArray())
        {
            var name = current.GetProperty("name").GetString()!;
            var type = current.GetProperty("type").GetString()!;
            if (ExternallyManaged(name, type) && type == "TXT")
            {
                // Existing local TXT entries predate native mail setup; merge only missing values.
                if (desired.Remove((name, type), out var local))
                {
                    var existing = current.GetProperty("records").EnumerateArray().Select(r => new Entry(r.GetProperty("content").GetString()!, r.GetProperty("disabled").GetBoolean())).ToArray();
                    var merged = existing.Concat(local.records.Where(r => !existing.Any(e => e.content == r.content))).ToArray();
                    if (merged.Length != existing.Length) changes.Add(new(name, type, current.GetProperty("ttl").GetInt32(), "REPLACE", merged));
                }
                continue;
            }
            if (ExternallyManaged(name, type) || type == "SOA" || (type == "TXT" && name.Split('.').Contains("_acme-challenge"))) { desired.Remove((name, type)); continue; } // PowerDNS owns the SOA and serial.
            if (!desired.Remove((name, type), out var target))
                changes.Add(new(name, type, 0, "DELETE", []));
            else
            {
                var existing = current.GetProperty("records").EnumerateArray()
                    .Select(r => new Entry(r.GetProperty("content").GetString()!, r.GetProperty("disabled").GetBoolean()))
                    .OrderBy(r => r.content, StringComparer.Ordinal).ToArray();
                if (current.GetProperty("ttl").GetInt32() != target.ttl || !existing.SequenceEqual(target.records))
                    changes.Add(target);
            }
        }
        changes.AddRange(desired.Values.Where(x => x.type != "TXT" || !x.name.Split('.').Contains("_acme-challenge")));
        if (changes.Count == 0) return;
        using var patch = await http.PatchAsJsonAsync(PathFor(zone), new { rrsets = changes }, ct);
        patch.EnsureSuccessStatusCode();
    }
    public async Task DeleteAsync(string zone, CancellationToken ct)
    {
        using var response = await http.DeleteAsync(PathFor(zone), ct);
        if (response.StatusCode != HttpStatusCode.NotFound) response.EnsureSuccessStatusCode();
    }
}
