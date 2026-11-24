using System.Net;
using System.Net.Sockets;
namespace HostingPanel.Api.Adapters;

public sealed record DnsRecord(string Type, string Name, string Value, string? Ttl = null);
public sealed record DnsChange(string Action, DnsRecord Record, DnsRecord? Previous = null);
public sealed record AdminDnsZone(string Domain, bool? LocalData, bool? LocalMail);

public static class DnsRecordValidation
{
    public static readonly string[] Types =
        ["A", "AAAA", "CNAME", "MX", "TXT", "SRV", "CAA", "NS", "PTR", "DS", "TLSA", "HTTPS", "SVCB"];
    public static bool Valid(DnsRecord r)
    {
        if (r is null || !Types.Contains(r.Type, StringComparer.Ordinal) || string.IsNullOrWhiteSpace(r.Name) ||
            string.IsNullOrWhiteSpace(r.Value) ||
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

}
