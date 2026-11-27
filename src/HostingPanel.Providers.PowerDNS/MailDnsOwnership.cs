namespace HostingPanel.Providers.PowerDNS;

// Shared TXT RRsets are preserved by ordinary zone reconciliation; explicit user edits are allowed.
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
