using Microsoft.AspNetCore.Http;
namespace HostingPanel.Api;

// Internal dispatch contract; the public routes remain the pinned DirectAdmin API.
public interface ICertificateProvider
{
    Task HandleCertificateRequestAsync(HttpContext context);
}
public sealed record DomainAcmeConfiguration(
    Dictionary<string,string> DnsEnvironment, string DnsProvider, bool Enabled,
    string ExternalAccountHMAC, string ExternalAccountKeyID, string KeyType,
    bool PreferWildcard, string Provider, string[] SkipDNSNames)
{
    public static DomainAcmeConfiguration Default => new(new(), "", false, "", "", "ec256", false, "", []);
}
