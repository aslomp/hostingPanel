using System.Net;
using System.Text.Json;

namespace HostingPanel.Web.Services;

/// <summary>Parses DirectAdmin's legacy application/x-www-form-urlencoded responses.</summary>
public static class LegacyApiResponse
{
    public static IReadOnlyDictionary<string, string> ParseFields(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            throw new FormatException("DirectAdmin retourneerde geen gegevens.");

        // An explicit JSON format is also valid for some DirectAdmin versions.
        if (body.TrimStart().StartsWith('{'))
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new FormatException("Een DirectAdmin-object werd verwacht.");
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    throw new FormatException("Geneste JSON wordt niet ondersteund door deze legacy-parser.");
                result[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString() ?? "" : prop.Value.ToString();
            }
            ThrowOnError(result);
            return result;
        }

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = entry.IndexOf('=');
            if (split < 0)
                throw new FormatException("DirectAdmin retourneerde ongeldige URL-gecodeerde gegevens.");
            var key = WebUtility.UrlDecode(entry[..split]);
            var value = WebUtility.UrlDecode(entry[(split + 1)..]);
            if (string.IsNullOrWhiteSpace(key))
                throw new FormatException("DirectAdmin retourneerde een veld zonder naam.");
            fields[key] = value;
        }
        if (fields.Count == 0)
            throw new FormatException("DirectAdmin retourneerde geen geldige velden.");
        ThrowOnError(fields);
        return fields;
    }

    // Legacy arrays are commonly returned as list[]=a&list[]=b, or list[0]=a.
    public static IReadOnlyList<string> ParseList(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return [];
        if (body.TrimStart().StartsWith('['))
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                throw new FormatException("Een lijst werd verwacht.");
            return doc.RootElement.EnumerateArray().Select(e =>
            {
                if (e.ValueKind != JsonValueKind.String)
                    throw new FormatException("Onverwacht record in DirectAdmin-lijst.");
                return e.GetString() ?? "";
            }).ToArray();
        }

        var list = new List<string>();
        var other = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = entry.IndexOf('=');
            if (split < 0) throw new FormatException("Ongeldige DirectAdmin-lijst.");
            var key = WebUtility.UrlDecode(entry[..split]);
            var value = WebUtility.UrlDecode(entry[(split + 1)..]);
            if (key.Equals("list[]", StringComparison.OrdinalIgnoreCase)
                || (key.StartsWith("list[", StringComparison.OrdinalIgnoreCase) && key.EndsWith(']')))
                list.Add(value);
            else
                other[key] = value;
        }
        ThrowOnError(other);
        if (list.Count != 0) return list;
        // Some releases return a plain numeric/username-keyed URL-encoded array.
        return other.Values.ToArray();
    }

    private static void ThrowOnError(IReadOnlyDictionary<string, string> fields)
    {
        if (fields.TryGetValue("error", out var error) && error is "1" or "true")
        {
            fields.TryGetValue("text", out var text);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(text)
                ? "DirectAdmin heeft het verzoek geweigerd." : $"DirectAdmin: {text}");
        }
    }
}
