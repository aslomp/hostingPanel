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
    FileAccessService FileAccess { get; }
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
    FileAccessService fileAccess,
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
    public FileAccessService FileAccess => fileAccess;
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
    /// <summary>
    /// Forward an exact Swagger route without changing its method, URL, query,
    /// credentials, request content or upstream response status.
    /// </summary>
    private static bool IsHopByHop(string header) =>
        header.Equals("Connection", StringComparison.OrdinalIgnoreCase)
        || header.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase)
        || header.Equals("Proxy-Authenticate", StringComparison.OrdinalIgnoreCase)
        || header.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
        || header.Equals("TE", StringComparison.OrdinalIgnoreCase)
        || header.Equals("Trailer", StringComparison.OrdinalIgnoreCase)
        || header.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
        || header.Equals("Upgrade", StringComparison.OrdinalIgnoreCase);

    protected static async Task ForwardToBackendAsync(HttpContext context, IHttpClientFactory clients)
    {
        if (!TryGetBasic(context, out var auth))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var rawPath = context.Request.Path.Value ?? string.Empty;
        if (!rawPath.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
            || rawPath.Contains("..", StringComparison.Ordinal)
            || rawPath.Contains('\\') || rawPath.Any(char.IsControl))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var request = new HttpRequestMessage(
            new HttpMethod(context.Request.Method), rawPath.TrimStart('/') + context.Request.QueryString);
        request.Headers.Authorization = auth;
        if (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
            request.Content = new StreamContent(context.Request.Body);

        // Preserve Swagger-defined request semantics, including Accept, Range and
        // multipart upload headers. Exclude only hop-by-hop transport headers.
        foreach (var (key, values) in context.Request.Headers)
        {
            if (key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                || key.Equals("Host", StringComparison.OrdinalIgnoreCase)
                || IsHopByHop(key)
                || key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!request.Headers.TryAddWithoutValidation(key, (IEnumerable<string>)values))
                request.Content?.Headers.TryAddWithoutValidation(key, (IEnumerable<string>)values);
        }

        try
        {
            using var response = await clients.CreateClient("legacy-directadmin")
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
            context.Response.StatusCode = (int)response.StatusCode;
            foreach (var (key, values) in response.Headers)
                if (!IsHopByHop(key))
                    context.Response.Headers[key] = values.ToArray();
            foreach (var (key, values) in response.Content.Headers)
                if (!IsHopByHop(key) && !key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    context.Response.Headers[key] = values.ToArray();
            await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        }
        catch (TaskCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            context.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
        }
        catch (HttpRequestException)
        {
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
        }
    }

}
