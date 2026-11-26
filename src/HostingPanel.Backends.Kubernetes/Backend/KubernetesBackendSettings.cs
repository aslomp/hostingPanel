using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace HostingPanel.Api;

public enum KubernetesIngressProvider
{
    [Display(Name = "NGINX Ingress")]
    NginxIngress,
    [Display(Name = "Envoy Proxy (Gateway API)")]
    EnvoyProxy
}

public sealed record HostingIpAddress(string Address, int PrefixLength, string Status, string Device, string NameServer, bool Global = false, string[]? Resellers = null, string[]? AssignedUsers = null);

public sealed record AcmeIssuerMapping(string Id, string Name, string IssuerName, string IssuerKind = "ClusterIssuer", bool Staging = false, bool SupportsWildcard = false);
public sealed class KubernetesBackendSettings : IBackendSettings
{
    [Display(Name = "Ingress provider", Description = "Desired routing implementation for hosted domains. Reconciliation is not implemented yet.")]
    public KubernetesIngressProvider IngressProvider { get; set; } = KubernetesIngressProvider.NginxIngress;

    [Display(Name = "Automatically create DNS zones", Description = "Create DNS zones for new hosting domains when PowerDNS is installed.")]
    public bool DnsAutoCreateZones { get; set; } = true;

    [Display(Name = "Public web IP", Description = "Public IPv4 or IPv6 address of the web ingress, not the DNS server.")]
    public string DnsWebIp { get; set; } = "";

    [Display(Name = "Primary nameserver", Description = "Fully qualified hostname of the primary authoritative nameserver.")]
    public string DnsNs1 { get; set; } = "";

    [Display(Name = "Secondary nameserver", Description = "Fully qualified hostname of the secondary authoritative nameserver.")]
    public string DnsNs2 { get; set; } = "";

    [Display(Name = "IP addresses", Description = "Managed hosting addresses.")]
    public HostingIpAddress[] IpAddresses { get; set; } = [];

    [Display(Name = "ACME providers", Description = "Map provider IDs to existing cert-manager Issuer or ClusterIssuer resources.")]
    public AcmeIssuerMapping[] AcmeProviders { get; set; } = [];

    [Display(Name = "Default ACME provider", Description = "Provider ID inherited by accounts and domains.")]
    public string AcmeDefaultProvider { get; set; } = "";

    [Display(Name = "User ACME defaults", Description = "Map account usernames to provider IDs.")]
    public Dictionary<string,string> AcmeUserProviders { get; set; } = new();

    [Display(Name = "Package ACME defaults", Description = "Map package GUIDs to provider IDs.")]
    public Dictionary<string,string> AcmePackageProviders { get; set; } = new();

    [Display(Name = "TLS ListenerSet", Description = "Dedicated HostingPanel ListenerSet in the hosting namespace. Must match the Helm listenerSetName permission.")]
    public string TlsListenerSetName { get; set; } = "";

    [JsonIgnore]
    public IBackendSettingsValidator Validator { get; } = new KubernetesBackendSettingsValidator();
}

public sealed class KubernetesBackendSettingsValidator : IBackendSettingsValidator
{
    public Dictionary<string, string[]> Validate(IBackendSettings settings)
    {
        if (settings is not KubernetesBackendSettings typed)
            return new() { ["Values"] = ["Wrong settings contract."] };
        var errors = new Dictionary<string, string[]>();
        if (!Enum.IsDefined(typed.IngressProvider))
            errors[nameof(typed.IngressProvider)] = ["Select a supported ingress provider."];
        if (typed.DnsWebIp.Length != 0 && (!System.Net.IPAddress.TryParse(typed.DnsWebIp, out var ip) ||
            ip.AddressFamily is not (System.Net.Sockets.AddressFamily.InterNetwork or System.Net.Sockets.AddressFamily.InterNetworkV6)))
            errors[nameof(typed.DnsWebIp)] = ["Enter a valid IPv4 or IPv6 address."];
        foreach (var (key, value) in new[] { (nameof(typed.DnsNs1), typed.DnsNs1), (nameof(typed.DnsNs2), typed.DnsNs2) })
        {
            if (value.Length != 0 && (value.Length > 254 || Uri.CheckHostName(value.TrimEnd('.')) != UriHostNameType.Dns || !value.TrimEnd('.').Contains('.')))
                errors[key] = ["Enter a fully qualified nameserver hostname."];
        }
        if (typed.DnsNs1.Length != 0 && string.Equals(typed.DnsNs1.TrimEnd('.'), typed.DnsNs2.TrimEnd('.'), StringComparison.OrdinalIgnoreCase))
            errors[nameof(typed.DnsNs2)] = ["Nameservers must be distinct."];
        var unique = new HashSet<string>(StringComparer.Ordinal);
        if (typed.IpAddresses.Length > 1024)
            errors[nameof(typed.IpAddresses)] = ["Too many IP addresses."];
        foreach (var entry in typed.IpAddresses)
        {
            if (entry is null || !System.Net.IPAddress.TryParse(entry.Address, out var address) ||
                entry.PrefixLength < 0 || entry.PrefixLength > (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128) ||
                entry.Status is not ("server" or "shared" or "available" or "owned") ||
                entry.Address != address.ToString() || !unique.Add(address.ToString()) || entry.Device is null || entry.Device.Length > 128 ||
                (entry.Resellers?.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 64) ?? false) || (entry.AssignedUsers?.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 64) ?? false) ||
                (entry.Status == "owned" && (entry.Global || (entry.Resellers?.Length ?? 0) != 0 || (entry.AssignedUsers?.Length ?? 0) != 1)) ||
                (entry.Status == "available" && (entry.AssignedUsers?.Length ?? 0) != 0) || entry.NameServer is null || (entry.NameServer.Length > 0 && Uri.CheckHostName(entry.NameServer.TrimEnd('.')) != UriHostNameType.Dns))
                errors[nameof(typed.IpAddresses)] = ["Invalid or duplicate IP address, prefix, status or nameserver."];
        }
        if (typed.IpAddresses.Length > 0 && typed.DnsWebIp.Length > 0 &&
            !typed.IpAddresses.Any(x => x is not null && x.Address == typed.DnsWebIp && x.Status is "server" or "shared"))
            errors[nameof(typed.DnsWebIp)] = ["Select a server or shared address from IP management."];
        var providerIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var provider in typed.AcmeProviders)
        {
            if (provider is null || !DnsLabel(provider.Id) || !DnsLabel(provider.IssuerName) ||
                string.IsNullOrWhiteSpace(provider.Name) || provider.Name.Length > 128 ||
                provider.IssuerKind is not ("Issuer" or "ClusterIssuer") || !providerIds.Add(provider.Id))
                errors[nameof(typed.AcmeProviders)] = ["Invalid or duplicate provider ID, issuer name or kind."];
        }
        if (typed.AcmeDefaultProvider.Length > 0 && !providerIds.Contains(typed.AcmeDefaultProvider))
            errors[nameof(typed.AcmeDefaultProvider)] = ["Unknown default provider."];
        if (typed.AcmeUserProviders.Any(x => !providerIds.Contains(x.Value)))
            errors[nameof(typed.AcmeUserProviders)] = ["Unknown user provider."];
        if (typed.AcmePackageProviders.Any(x => !Guid.TryParse(x.Key, out _) || !providerIds.Contains(x.Value)))
            errors[nameof(typed.AcmePackageProviders)] = ["Invalid package GUID or unknown provider."];
        if (typed.TlsListenerSetName.Length > 0 && !DnsLabel(typed.TlsListenerSetName))
            errors[nameof(typed.TlsListenerSetName)] = ["Invalid ListenerSet name."];
        return errors;
    }
    private static bool DnsLabel(string? s) => s is { Length: > 0 and <= 63 } && char.IsAsciiLetterOrDigit(s[0]) && char.IsAsciiLetterOrDigit(s[^1]) && s.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'z' or '-');
}
