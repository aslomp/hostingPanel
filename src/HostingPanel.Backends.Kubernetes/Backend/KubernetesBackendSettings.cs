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

public sealed record HostingIpAddress(string Address, int PrefixLength, string Status, string Device, string NameServer);

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
                entry.Status is not ("server" or "shared" or "available") ||
                !unique.Add(address.ToString()) || entry.Device is null || entry.Device.Length > 128 ||
                entry.NameServer is null || (entry.NameServer.Length > 0 && Uri.CheckHostName(entry.NameServer.TrimEnd('.')) != UriHostNameType.Dns))
                errors[nameof(typed.IpAddresses)] = ["Invalid or duplicate IP address, prefix, status or nameserver."];
        }
        if (typed.IpAddresses.Length > 0 && typed.DnsWebIp.Length > 0 &&
            !typed.IpAddresses.Any(x => x is not null && x.Address == typed.DnsWebIp && x.Status is "server" or "shared"))
            errors[nameof(typed.DnsWebIp)] = ["Select a server or shared address from IP management."];
        return errors;
    }
}
