using System.Globalization;
using System.Text.Json;
using System.Net;

namespace HostingPanel.Api.Adapters;

public static class LegacyDomainDetailsAdapter
{
    public static IReadOnlyDictionary<string,string> Parse(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) throw new FormatException("Empty domain detail response.");
        var fields = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        if (payload.TrimStart().StartsWith('{'))
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new FormatException("Expected domain settings object.");
            foreach (var property in doc.RootElement.EnumerateObject())
                if (property.Value.ValueKind is JsonValueKind.String or JsonValueKind.True
                    or JsonValueKind.False or JsonValueKind.Number)
                    fields[property.Name] = property.Value.ToString();
        }
        else
        {
            foreach(var pair in payload.Split('&',StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = pair.IndexOf('=');
                if(separator < 1) throw new FormatException("Malformed DirectAdmin domain details.");
                fields[WebUtility.UrlDecode(pair[..separator])] =
                    WebUtility.UrlDecode(pair[(separator + 1)..]);
            }
        }
        if (fields.TryGetValue("error",out var error) && error is "1" or "true" or "True")
            throw new LegacyApiException("DirectAdmin rejected the domain details request.");
        if (fields.Count == 0 || !fields.ContainsKey("domain"))
            throw new FormatException("Domain details lack the expected domain field.");
        return fields;
    }

    // Single-setting modifications are deliberate: legacy action=modify treats omitted
    // checkboxes as OFF unless only_affect is provided.
    public static string EncodeSetting(string domain, string setting, string value)
    {
        if (!LegacyDomainsAdapter.IsValidDomain(domain))
            throw new ArgumentException("Invalid domain.",nameof(domain));
        if (setting is not ("ssl" or "php" or "cgi" or "bandwidth" or "quota"))
            throw new ArgumentException("Unsupported domain setting.",nameof(setting));

        if(setting is "ssl" or "php" or "cgi")
        {
            if(value is not ("ON" or "OFF"))
                throw new ArgumentException("Expected ON or OFF.",nameof(value));
        }
        else if (!value.Equals("unlimited",StringComparison.OrdinalIgnoreCase) &&
            (!decimal.TryParse(value,NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,out var number) || number < 0 || number > 1_000_000_000m))
            throw new ArgumentException("Expected nonnegative MiB limit or unlimited.",nameof(value));

        return "action=modify&domain=" + Uri.EscapeDataString(domain)
             + "&only_affect=" + Uri.EscapeDataString(setting)
             + "&" + Uri.EscapeDataString(setting) + "=" + Uri.EscapeDataString(value);
    }

    public static bool IsAllowedValue(string setting, string value)
    {
        try { _ = EncodeSetting("test.xyz1", setting, value); return true; }
        catch (ArgumentException) { return false; }
    }
}
