namespace HostingPanel.Providers.PowerDNS;

// Reserve entire RRsets, not individual TXT values: PowerDNS replaces RRsets
// atomically and has no cross-controller compare-and-swap. All TXT RRsets
// in a mail-enabled zone are reserved because Stalwart may publish SPF at
// arbitrary in-zone exchanger hostnames. Website A/AAAA/NS/CNAME remain local.
public static class MailDnsOwnership
{
    public static IReadOnlySet<string> PublishRecords { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "mx", "spf", "dkim", "dmarc" };

    public static bool Reserved(string domain, string name, string type)
    {
        var zone = domain.TrimEnd('.');
        var owner = name.TrimEnd('.');
        return type.Equals("TXT", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("MX", StringComparison.OrdinalIgnoreCase) &&
            (owner == "@" || owner.Equals(zone, StringComparison.OrdinalIgnoreCase));
    }
}
