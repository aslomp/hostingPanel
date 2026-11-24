using System.Text.Json;

namespace HostingPanel.Api.Adapters;

public static class AdminDnsZonesAdapter
{
    public static IReadOnlyList<AdminDnsZone> Parse(string response)
    {
        using var doc = JsonDocument.Parse(response);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new FormatException("Invalid admin DNS list.");
        if (root.TryGetProperty("error",out var error) && error.ToString() is "1" or "true")
            throw new LegacyApiException("DirectAdmin refused admin DNS list.");

        // Legacy DirectAdmin table JSON uses numeric keys for rows and metadata
        // keys such as 'info'. Never mistake those metadata keys for domains.
        var result = new List<AdminDnsZone>();
        foreach (var entry in root.EnumerateObject())
        {
            if (!entry.Name.All(char.IsAsciiDigit) || entry.Value.ValueKind != JsonValueKind.Object)
                continue;
            if (!entry.Value.TryGetProperty("domain", out var d) ||
                d.ValueKind != JsonValueKind.String) continue;
            var domain = d.GetString() ?? "";
            if (!LegacyDomainsAdapter.IsValidDomain(domain))
                throw new FormatException("Invalid DNS zone name in DirectAdmin response.");
            result.Add(new AdminDnsZone(domain,
                Flag(entry.Value,"local_data"), Flag(entry.Value,"local_mail")));
        }
        return result.DistinctBy(x => x.Domain,StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x.Domain,StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool? Flag(JsonElement record, string property)
    {
        if (!record.TryGetProperty(property,out var value)) return null;
        return value.ToString().ToLowerInvariant() switch
        {
            "yes" or "true" or "on" or "1" => true,
            "no" or "false" or "off" or "0" => false,
            _ => null
        };
    }
}
