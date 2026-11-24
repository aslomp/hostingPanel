using System.Text.Json;
using System.Text.RegularExpressions;

namespace HostingPanel.Api.Adapters;

public static class LegacyDomainsAdapter
{
    // General domain validation. The .xyz1 suffix is only used in test fixtures.
    public static bool IsValidDomain(string? domain) =>
        domain is not null && domain.Length <= 253 &&
        Regex.IsMatch(domain, @"^(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z](?:[a-z0-9-]{0,61}[a-z0-9])?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static IReadOnlyList<string> ParseDomains(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return [];
        if (payload.TrimStart().StartsWith('['))
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new FormatException("Expected DirectAdmin domain array.");
            return Normalized(document.RootElement.EnumerateArray()
                .Select(e => e.ValueKind == JsonValueKind.String
                    ? e.GetString() ?? "" : throw new FormatException("Invalid domain array item.")));
        }
        if (payload.TrimStart().StartsWith('{'))
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new FormatException("Invalid domain response.");
            if (root.TryGetProperty("error", out var error) &&
                (error.ToString() is "1" or "true" or "True"))
                throw new LegacyApiException("DirectAdmin rejected the domain request.");
            if (root.TryGetProperty("list", out var list) && list.ValueKind == JsonValueKind.Array)
                return Normalized(list.EnumerateArray().Select(e =>
                    e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" :
                    throw new FormatException("Invalid domain list item.")));
            // CMD_API_SHOW_USER_DOMAINS uses domain names as keys with colon-separated statistics.
            return Normalized(root.EnumerateObject()
                .Where(p => (p.Name.All(char.IsAsciiDigit) && p.Value.ValueKind == JsonValueKind.String)
                    || (p.Name.Contains('.') && !p.Name.StartsWith("pointer_", StringComparison.OrdinalIgnoreCase)))
                .Select(p => p.Name.Contains('.') ? p.Name : p.Value.GetString() ?? ""));
        }

        var domains = new List<string>();
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in payload.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var position = pair.IndexOf('=');
            if (position < 1) throw new FormatException("Malformed DirectAdmin domain response.");
            var key = System.Net.WebUtility.UrlDecode(pair[..position]);
            var value = System.Net.WebUtility.UrlDecode(pair[(position + 1)..]);
            if (key.Equals("list[]", StringComparison.OrdinalIgnoreCase) ||
                (key.StartsWith("list[", StringComparison.OrdinalIgnoreCase) && key.EndsWith(']')) ||
                key.All(char.IsAsciiDigit))
                domains.Add(value);
            else if (key.Contains('.') && !key.StartsWith("pointer_", StringComparison.OrdinalIgnoreCase))
                domains.Add(key);
            else errors[key] = value;
        }
        if (errors.TryGetValue("error", out var err) && err is "1" or "true")
            throw new LegacyApiException("DirectAdmin rejected the domain request.");
        return Normalized(domains);
    }

    public static string EncodeCreateForm(string domain, bool php, bool ssl, bool cgi) =>
        string.Join("&", new [] {
            "action=create",
            "domain=" + Uri.EscapeDataString(domain),
            "ubandwidth=unlimited",
            "uquota=unlimited",
            "php=" + (php ? "ON" : "OFF"),
            "ssl=" + (ssl ? "ON" : "OFF"),
            "cgi=" + (cgi ? "ON" : "OFF")
        });

    public static string EncodeModifyForm(string domain, bool php, bool ssl, bool cgi) =>
        EncodeCreateForm(domain, php, ssl, cgi).Replace("action=create", "action=modify", StringComparison.Ordinal);

    public static string EncodeDeleteForm(string domain) =>
        "delete=yes&confirmed=yes&select0=" + Uri.EscapeDataString(domain);

    public static void AssertMutationSucceeded(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            throw new FormatException("DirectAdmin returned an empty mutation response.");

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (body.TrimStart().StartsWith('{'))
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new FormatException("Expected an object response.");
            foreach (var item in doc.RootElement.EnumerateObject())
                if (item.Value.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object))
                    fields[item.Name] = item.Value.ToString();
        }
        else
        {
            foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var i = pair.IndexOf('=');
                if (i < 1) throw new FormatException("Unexpected DirectAdmin mutation response.");
                fields[System.Net.WebUtility.UrlDecode(pair[..i])] =
                    System.Net.WebUtility.UrlDecode(pair[(i + 1)..]);
            }
        }

        if (!fields.TryGetValue("error", out var error))
            throw new FormatException("DirectAdmin did not return a success indicator.");

        if (error is "1" or "true" or "True")
            throw new LegacyApiException("DirectAdmin rejected the requested domain operation.");

        if (error is not ("0" or "false" or "False"))
            throw new FormatException("Unexpected DirectAdmin success indicator.");
    }

    private static IReadOnlyList<string> Normalized(IEnumerable<string> domains)
    {
        var result = new List<string>();
        foreach (var domain in domains)
        {
            if (string.IsNullOrWhiteSpace(domain) || domain.Length > 253 ||
                domain.Contains('/') || domain.Contains('\\') || domain.Any(char.IsControl))
                throw new FormatException("Legacy server returned an invalid domain.");
            result.Add(domain);
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
