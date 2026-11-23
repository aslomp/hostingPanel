using System.Net.Http.Headers;
using HostingPanel.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace HostingPanel.Api;

/// <summary>A request-scoped backend facade. Controllers never construct transport adapters.</summary>
public interface IHostingBackend
{
    AccountCatalogService AccountCatalog { get; }
    AccessibleDomainsService AccessibleDomains { get; }
    LegacyUserDetailsService LegacyUserDetails { get; }
    LegacyAccountOverviewService LegacyAccountOverview { get; }
    ImpersonationService Impersonation { get; }
    DatabaseManagementService Databases { get; }
    DomainManagementService Domains { get; }
    DnsZoneService Dns { get; }
    IHttpClientFactory Clients { get; }
}

public sealed class DirectAdminBackend(
    AccountCatalogService accountCatalog,
    AccessibleDomainsService accessibleDomains,
    LegacyUserDetailsService legacyUserDetails,
    LegacyAccountOverviewService legacyAccountOverview,
    ImpersonationService impersonation,
    DatabaseManagementService databases,
    DomainManagementService domains,
    DnsZoneService dns,
    IHttpClientFactory clients) : IHostingBackend
{
    public AccountCatalogService AccountCatalog => accountCatalog;
    public AccessibleDomainsService AccessibleDomains => accessibleDomains;
    public LegacyUserDetailsService LegacyUserDetails => legacyUserDetails;
    public LegacyAccountOverviewService LegacyAccountOverview => legacyAccountOverview;
    public ImpersonationService Impersonation => impersonation;
    public DatabaseManagementService Databases => databases;
    public DomainManagementService Domains => domains;
    public DnsZoneService Dns => dns;
    public IHttpClientFactory Clients => clients;
}

public abstract class BackendController : ControllerBase
{
    protected static bool TryGetBasic(HttpContext context, out AuthenticationHeaderValue authentication)
    {
        authentication = null!;
        if (!AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization.ToString(), out var value)
            || !value.Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(value.Parameter))
        {
            context.Response.Headers.WWWAuthenticate = "Basic realm=\"HostingPanel API\"";
            return false;
        }
        authentication = value;
        return true;
    }
}
