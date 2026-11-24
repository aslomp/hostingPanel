using System.Net.Http.Headers;
using HostingPanel.Api.Services;
using HostingPanel.Api.Adapters;
using Microsoft.AspNetCore.Mvc;

namespace HostingPanel.Api;

/// <summary>A request-scoped backend facade. Controllers never construct transport adapters.</summary>
public sealed class DirectAdminBackend(
    AccountCatalogService accountCatalog,
    AccountMutationService accountMutations,
    AccessibleDomainsService accessibleDomains,
    LegacyUserDetailsService legacyUserDetails,
    LegacyAccountOverviewService legacyAccountOverview,
    ImpersonationService impersonation,
    DatabaseManagementService databases,
    AccessibleDatabasesService accessibleDatabases,
    DomainManagementService domains,
    DnsZoneService dns,
    FileAccessService fileAccess,
    TicketService tickets,
    IHttpClientFactory clients) : IHostingBackend
{
    public IAccountCatalog AccountCatalog => accountCatalog;
    public IAccountMutations AccountMutations => accountMutations;
    public IAccessibleDomains AccessibleDomains => accessibleDomains;
    public IUserDetails UserDetails => legacyUserDetails;
    public IAccountOverview AccountOverview => legacyAccountOverview;
    public IImpersonation Impersonation => impersonation;
    public IDatabases Databases => databases;
    public IAccessibleDatabases AccessibleDatabases => accessibleDatabases;
    public IDomains Domains => domains;
    public IWebHosting WebHosting => domains;
    public IDNSProvider Dns => dns;
    public IFileAccess FileAccess => fileAccess;
    public ITickets Tickets => tickets;
    public IResult PreviewDomain(DomainCreatePreview input)
    {
        if (!LegacyDomainsAdapter.IsValidDomain(input.Domain))
            return Results.Json(new { type = "INVALID_DOMAIN" }, statusCode: 400);
        return Results.Json(new {
            domain = input.Domain.ToLowerInvariant(),
            dryRun = true,
            legacyEndpoint = "CMD_API_DOMAIN",
            method = "POST",
            form = LegacyDomainsAdapter.EncodeCreateForm(input.Domain.ToLowerInvariant(), input.Php, input.Ssl, input.Cgi),
            executed = false
        });
    }
    public async Task<int> AuthenticateSessionAsync(AuthenticationHeaderValue auth, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/session");
        request.Headers.Authorization = auth;
        try
        {
            using var response = await clients.CreateClient("legacy-directadmin").SendAsync(request, ct);
            return (int)response.StatusCode;
        }
        catch (HttpRequestException) { return 502; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return 504; }
    }
    public Task ForwardAsync(HttpContext context) => DirectAdminTransport.ForwardAsync(context, clients);
}


public static class DirectAdminTransport
{
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

    public static async Task ForwardAsync(HttpContext context, IHttpClientFactory clients)
    {
        // Authentication and authorization for official DirectAdmin routes belong
        // to the backend. In particular, login/logout may not have Basic credentials.
        // Forward Authorization (of any scheme) and cookies unchanged.

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
        if (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
            request.Content = new StreamContent(context.Request.Body);

        // Preserve Swagger-defined request semantics, including Accept, Range and
        // multipart upload headers. Exclude only hop-by-hop transport headers.
        foreach (var (key, values) in context.Request.Headers)
        {
            if (key.Equals("Host", StringComparison.OrdinalIgnoreCase)
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
