using System.Text.RegularExpressions;
using HostingPanel.Api.Services;

namespace HostingPanel.Api;

internal static class StalwartDnsRecords
{
    // Only the mail RRsets requested for this integration are accepted.
    // Unknown syntax in a supported record fails the entire import.
    internal static IReadOnlyList<DnsRecord> Parse(string zone, string text)
    {
        var result = new List<DnsRecord>();
        var origin = zone.TrimEnd('.').ToLowerInvariant();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
            if (line.StartsWith("$ORIGIN", StringComparison.OrdinalIgnoreCase))
            {
                if (!line[7..].Trim().TrimEnd('.').Equals(origin, StringComparison.OrdinalIgnoreCase))
                    throw new FormatException("Unexpected mail DNS origin.");
                continue;
            }
            if (line.StartsWith("$TTL", StringComparison.OrdinalIgnoreCase)) continue;
            var match = Regex.Match(line, @"^(\S+)\s+(?:(\d+)\s+)?(?:IN\s+)?(MX|TXT)\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success)
            {
                if (Regex.IsMatch(line, @"\s(?:MX|TXT)\s", RegexOptions.IgnoreCase)) throw new FormatException("Unsupported mail DNS syntax.");
                continue;
            }
            var owner = match.Groups[1].Value.ToLowerInvariant();
            if (owner == "@") owner = origin + ".";
            else if (!owner.EndsWith('.')) owner = owner == origin || owner.EndsWith("." + origin) ? owner + "." : owner + "." + origin + ".";
            if (owner != origin + "." && !owner.EndsWith("." + origin + ".", StringComparison.Ordinal))
                throw new FormatException("Mail DNS owner outside zone.");
            var type = match.Groups[3].Value.ToUpperInvariant();
            var value = match.Groups[4].Value.Trim();
            var ttl = match.Groups[2].Success ? match.Groups[2].Value : "3600";
            if (!int.TryParse(ttl, out var seconds) || seconds <= 0) throw new FormatException("Invalid mail DNS TTL.");
            if (type == "MX")
            {
                if (owner != origin + "." || !Regex.IsMatch(value, @"^\d{1,5}\s+[a-zA-Z0-9.-]+\.$"))
                    throw new FormatException("Invalid mail MX record.");
            }
            else
            {
                if (!Regex.IsMatch(value, "^\x22[^\x22\\\\\r\n]*\x22(?:\\s+\x22[^\x22\\\\\r\n]*\x22)*$"))
                    throw new FormatException("Unsupported mail TXT syntax.");
                var plain = string.Concat(Regex.Matches(value, "\x22([^\x22]*)\x22").Select(x => x.Groups[1].Value));
                if (!(owner == origin + "." && plain.StartsWith("v=spf1", StringComparison.OrdinalIgnoreCase)
                    || owner == "_dmarc." + origin + "." && plain.StartsWith("v=DMARC1", StringComparison.OrdinalIgnoreCase)
                    || owner.EndsWith("._domainkey." + origin + ".", StringComparison.Ordinal) && plain.StartsWith("v=DKIM1", StringComparison.OrdinalIgnoreCase))) continue;
            }
            result.Add(new DnsRecord(type, owner, value, ttl));
        }
        if (!result.Any(x => x.Type == "MX") || !result.Any(x => x.Type == "TXT" && x.Name.Contains("._domainkey.")))
            throw new FormatException("Mail DNS records are not ready.");
        return result.Distinct().ToArray();
    }
}
