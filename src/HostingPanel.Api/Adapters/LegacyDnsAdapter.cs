using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace HostingPanel.Api.Adapters;

public sealed record DnsRecord(string Type, string Name, string Value, string? Ttl = null);
public sealed record DnsChange(string Action, DnsRecord Record, DnsRecord? Previous = null);

public static class LegacyDnsAdapter
{
    public static readonly string[] Types =
        ["A", "AAAA", "CNAME", "MX", "TXT", "SRV", "CAA", "NS", "PTR", "DS", "TLSA"];

    public static IReadOnlyList<DnsRecord> ParseRecords(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) throw new FormatException("DNS response is empty.");
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new FormatException("DNS response must be an object.");
        if (root.TryGetProperty("error", out var error) && error.ToString() is "1" or "true")
            throw new InvalidOperationException("DirectAdmin rejected the DNS zone request.");
        if (!root.TryGetProperty("records", out var records) || records.ValueKind != JsonValueKind.Array)
            throw new FormatException("DirectAdmin did not return the DNS records array.");
        var result = new List<DnsRecord>();
        foreach (var item in records.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new FormatException("Invalid DNS record.");
            var type = item.GetProperty("type").GetString() ?? "";
            var name = item.TryGetProperty("index", out var index) ? index.GetString() ?? "" :
                item.GetProperty("name").GetString() ?? "";
            var value = item.GetProperty("value").GetString() ?? "";
            string? ttl = item.TryGetProperty("ttl", out var t) ? t.ToString() : null;
            if (!Types.Contains(type, StringComparer.Ordinal) || string.IsNullOrEmpty(name))
                throw new FormatException("Unsupported DNS record returned by DirectAdmin.");
            result.Add(new DnsRecord(type, name, value, ttl));
        }
        return result;
    }

    public static bool Valid(DnsRecord r)
    {
        if (!Types.Contains(r.Type, StringComparer.Ordinal) || string.IsNullOrWhiteSpace(r.Name) ||
            r.Name.Length > 253 || r.Value.Length > 4096 || r.Value.Any(char.IsControl) ||
            r.Name.Any(c => char.IsControl(c) || c is '&' or '=' or '/' or '\\' or '?'))
            return false;
        if (r.Ttl is not null && (!int.TryParse(r.Ttl, out var ttl) || ttl is < 60 or > 604800))
            return false;
        return r.Type switch
        {
            "A" => IPAddress.TryParse(r.Value, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork,
            "AAAA" => IPAddress.TryParse(r.Value, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6,
            "CNAME" or "NS" or "PTR" => !string.IsNullOrWhiteSpace(r.Value) && r.Value.Length <= 253,
            "MX" => r.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 1,
            _ => !string.IsNullOrWhiteSpace(r.Value)
        };
    }

    public static bool Identical(DnsRecord left, DnsRecord right) =>
        left.Type == right.Type && string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase)
            && left.Value == right.Value;

    // DirectAdmin selection values must be URL encoded as ONE form field. Encoding the
    // inner '=' and '&' separately prevents accidentally selecting all values of one name.
    public static Dictionary<string,string> Form(string domain, DnsChange change)
    {
        if (!LegacyDomainsAdapter.IsValidDomain(domain) || !Valid(change.Record))
            throw new ArgumentException("Invalid DNS change.");
        if (change.Action is not ("add" or "edit" or "delete"))
            throw new ArgumentException("Invalid DNS action.");
        if (change.Action != "add" && (change.Previous is null || !Valid(change.Previous)))
            throw new ArgumentException("Previous record required for edit/delete.");

        var form = new Dictionary<string,string> {
            ["domain"] = domain,
            ["action"] = change.Action == "delete" ? "select" : change.Action
        };
        if (change.Previous is { } previous)
        {
            var prefix = previous.Type.ToLowerInvariant() + "recs0";
            // The outer URL form encoder must escape the whole compound value.
            form[prefix] = "name=" + previous.Name + "&value=" + previous.Value;
        }
        if (change.Action != "delete")
        {
            form["type"] = change.Record.Type;
            form["name"] = change.Record.Name;
            form["value"] = change.Record.Value;
            if (change.Record.Ttl is not null) form["ttl"] = change.Record.Ttl;
        }
        return form;
    }

    public static bool Applied(IReadOnlyList<DnsRecord> before,
        IReadOnlyList<DnsRecord> after, DnsChange change)
    {
        var old = change.Previous;
        // A no-op edit is not a reliably observable mutation, but remains idempotent.
        return change.Action switch
        {
            "add" => after.Count(r => Identical(r,change.Record)) > before.Count(r => Identical(r,change.Record)),
            "delete" => old is not null &&
                after.Count(r => Identical(r,old)) < before.Count(r => Identical(r,old)),
            "edit" => old is not null &&
                (Identical(old,change.Record) ||
                (after.Count(r => Identical(r,old)) < before.Count(r => Identical(r,old)) &&
                 after.Count(r => Identical(r,change.Record)) > before.Count(r => Identical(r,change.Record)))),
            _ => false
        };
    }
}
