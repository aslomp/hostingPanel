using System.Net;
using System.Text.Json;
using HostingPanel.Api.Adapters;

namespace HostingPanel.Api.Adapters;

/// <summary>Legacy DirectAdmin lists may be JSON arrays or URL-encoded list[] entries.</summary>
public static class LegacyAccountListParser
{
    public static IReadOnlyList<string> Parse(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return [];
        var names = new List<string>();
        if (body.TrimStart().StartsWith('['))
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new FormatException("Expected accounts array.");
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.String) throw new FormatException("Invalid account entry.");
                names.Add(entry.GetString() ?? "");
            }
        }
        else if (body.TrimStart().StartsWith('{'))
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new FormatException("Expected account list.");
            if (root.TryGetProperty("error", out var error) && error.ToString() is "1" or "true")
                throw new LegacyApiException("DirectAdmin rejected account listing.");
            if (root.TryGetProperty("list", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in list.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.String) throw new FormatException("Invalid list entry.");
                    names.Add(entry.GetString() ?? "");
                }
            }
            else
            {
                foreach (var entry in root.EnumerateObject())
                    if (entry.Name.All(char.IsAsciiDigit) && entry.Value.ValueKind == JsonValueKind.String)
                        names.Add(entry.Value.GetString() ?? "");
            }
        }
        else
        {
            foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var index = pair.IndexOf('=');
                if (index < 1) throw new FormatException("Invalid form response.");
                var key = WebUtility.UrlDecode(pair[..index]);
                var value = WebUtility.UrlDecode(pair[(index + 1)..]);
                if (key.Equals("error", StringComparison.OrdinalIgnoreCase) && value is "1" or "true")
                    throw new LegacyApiException("DirectAdmin rejected account listing.");
                if (key == "list[]" ||
                    (key.StartsWith("list[", StringComparison.OrdinalIgnoreCase) && key.EndsWith(']')) ||
                    key.All(char.IsAsciiDigit))
                    names.Add(value);
            }
        }
        if (names.Any(name => !Services.LegacyAccountOverviewService.ValidUsername(name)))
            throw new FormatException("DirectAdmin returned an invalid account name.");
        return names.Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
