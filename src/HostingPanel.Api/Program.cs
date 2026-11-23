using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using HostingPanel.Api.Adapters;
using HostingPanel.Api.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddScoped<LegacyUserDetailsService>();
builder.Services.AddScoped<DomainManagementService>();
builder.Services.AddHttpClient("legacy-directadmin", client =>
{
    var configured = builder.Configuration["DirectAdmin:BaseUrl"]
        ?? throw new InvalidOperationException("DirectAdmin:BaseUrl is required.");
    var origin = new Uri(configured, UriKind.Absolute);
    if (origin.Scheme != Uri.UriSchemeHttps || origin.AbsolutePath != "/"
        || origin.UserInfo.Length != 0 || origin.Query.Length != 0 || origin.Fragment.Length != 0)
        throw new InvalidOperationException("DirectAdmin:BaseUrl must be a credential-free HTTPS origin.");
    client.BaseAddress = origin;
    client.Timeout = TimeSpan.FromSeconds(30);
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    AllowAutoRedirect = false,
    UseCookies = false,
    AutomaticDecompression = DecompressionMethods.None
});

var app = builder.Build();
app.UseHttpsRedirection();

// One server-side, global write gate for all present and future API mutations.
// Safe preview endpoint is intentionally excluded (it sends no write to DirectAdmin).
app.Use(async (context, next) =>
{
    var mutating = context.Request.Method is "POST" or "PUT" or "PATCH" or "DELETE";
    var preview = context.Request.Path.Equals("/hosting/domains/preview", StringComparison.OrdinalIgnoreCase);
    if (mutating && !preview && !context.RequestServices.GetRequiredService<IConfiguration>()
            .GetValue<bool>("HostingPanel:EnableMutations"))
    {
        context.Response.StatusCode = StatusCodes.Status409Conflict;
        await context.Response.WriteAsJsonAsync(new { type = "MUTATIONS_DISABLED" });
        return;
    }
    await next();
});
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

// Static published contract. The backend adapter is implemented incrementally.
app.MapGet("/openapi/directadmin.json", (IWebHostEnvironment environment) =>
{
    var filename = Path.Combine(environment.ContentRootPath, "spec", "directadmin.swagger.json");
    return File.Exists(filename)
        ? Results.File(filename, "application/json")
        : Results.Problem("Pinned OpenAPI specification unavailable.", statusCode: 503);
});

// Public modern API contract: GET /api/search/users -> JSON array of usernames.
// Internal legacy adapter: CMD_API_SHOW_ALL_USERS?json=yes.
// Each request is authorized by the upstream DirectAdmin account; never use shared admin credentials.
app.MapGet("/api/search/users", async (HttpContext context, IHttpClientFactory factory) =>
{
    if (!TryGetBasic(context, out var authentication)) return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);

    var q = context.Request.Query["q"].ToString();
    var rawLimit = context.Request.Query["limit"].ToString();
    var limit = 50;
    if (rawLimit.Length > 0 && (!int.TryParse(rawLimit, out limit) || limit is < 1 or > 1000))
        return Results.Json(new { type = "BAD_REQUEST" }, statusCode: 400);
    if (q.Length > 256)
        return Results.Json(new { type = "BAD_REQUEST" }, statusCode: 400);

    try
    {
        var client = factory.CreateClient("legacy-directadmin");
        using var upstreamRequest = new HttpRequestMessage(HttpMethod.Get, "CMD_API_SHOW_ALL_USERS?json=yes");
        upstreamRequest.Headers.Authorization = authentication;
        using var response = await client.SendAsync(upstreamRequest, context.RequestAborted);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return Results.Json(new { type = response.StatusCode == HttpStatusCode.Forbidden ? "ACCESS_DENIED" : "UNAUTHORIZED" },
                statusCode: (int)response.StatusCode);
        if (!response.IsSuccessStatusCode)
            return Results.Json(new { type = "UPSTREAM_ERROR" }, statusCode: 502);
        var payload = await response.Content.ReadAsStringAsync(context.RequestAborted);
        var users = LegacyUsersAdapter.SearchUsers(LegacyUsersAdapter.ParseUsernames(payload), q, limit);
        return Results.Json(users);
    }
    catch (LegacyApiException) { return Results.Json(new { type = "ACCESS_DENIED" }, statusCode: 403); }
    catch (JsonException) { return Results.Json(new { type = "UPSTREAM_ERROR" }, statusCode: 502); }
    catch (FormatException) { return Results.Json(new { type = "UPSTREAM_ERROR" }, statusCode: 502); }
    catch (HttpRequestException) { return Results.Json(new { type = "UPSTREAM_UNAVAILABLE" }, statusCode: 502); }
    catch (TaskCanceledException) when (!context.RequestAborted.IsCancellationRequested)
    { return Results.Json(new { type = "UPSTREAM_TIMEOUT" }, statusCode: 504); }
});

// Modern DirectAdmin user detail endpoints backed by a legacy read-only adapter.
// The strict Swagger validator returns 501 if the old server lacks required fields.
// This is preferable to plausible-looking but incorrect account state.
app.MapGet("/api/users/{username}/config", (HttpContext context, string username,
    LegacyUserDetailsService service) => ReadUserDetail(context, username, "config", service));
app.MapGet("/api/users/{username}/usage", (HttpContext context, string username,
    LegacyUserDetailsService service) => ReadUserDetail(context, username, "usage", service));

static async Task<IResult> ReadUserDetail(HttpContext context, string username,
    string kind, LegacyUserDetailsService service)
{
    if (!TryGetBasic(context, out var auth))
        return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);
    if (username.Length is < 1 or > 64 || !username.All(c =>
        char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
        return Results.Json(new { type = "BAD_REQUEST" }, statusCode: 400);
    var result = await service.GetAsync(username, kind, auth, context.RequestAborted);
    return result.Result is not null
        ? Results.Json(result.Result)
        : Results.Json(new { type = result.Error ?? "UPSTREAM_ERROR" }, statusCode: result.Status);
}

// HostingPanel-specific business routes: not misrepresented as DirectAdmin Swagger routes.
// Reads current-account domains, or (for authorized admins/resellers) a selected user's domains.
app.MapGet("/hosting/domains", async (HttpContext context, DomainManagementService service) =>
{
    if (!TryGetBasic(context, out var auth))
        return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);
    var owner = context.Request.Query["user"].ToString();
    var selected = string.IsNullOrWhiteSpace(owner) ? null : owner;
    if (!DomainManagementService.ValidOwner(selected))
        return Results.Json(new { type = "BAD_REQUEST" }, statusCode: 400);

    var response = await service.ListAsync(selected, auth, context.RequestAborted);
    return response.Domains is not null
        ? Results.Json(new { owner = selected, domains = response.Domains })
        : Results.Json(new { type = response.Error ?? "UPSTREAM_ERROR" }, statusCode: response.Status);
});

// Explicitly a preview. No POST requests are sent to DirectAdmin or to its file system.
app.MapPost("/hosting/domains/preview", (HttpContext context, DomainCreatePreview input) =>
{
    if (!TryGetBasic(context, out _))
        return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);
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
});

// Domain mutations require exact-name confirmation and the global EnableMutations gate.
// DirectAdmin still enforces permissions for each authenticated account.
app.MapPost("/hosting/domains/execute", async (HttpContext context,
    DomainMutationRequest input, DomainManagementService service) =>
{
    if (!TryGetBasic(context, out var auth))
        return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);
    var result = await service.MutateAsync(input, auth, context.RequestAborted);
    return result.Error is null
        ? Results.Json(new { domain = input.Domain.ToLowerInvariant(), action = input.Action, executed = true })
        : Results.Json(new { type = result.Error }, statusCode: result.Status);
});

// Do not blindly proxy modern routes to an older DirectAdmin installation:
// that violates the pinned Swagger contract.
app.MapMethods("/api/{**path}", ["GET", "POST", "PUT", "PATCH", "DELETE"],
    (HttpContext context) =>
        TryGetBasic(context, out _)
            ? Results.Json(new { type = "NOT_IMPLEMENTED" }, statusCode: 501)
            : Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401));

app.Run();

static bool TryGetBasic(HttpContext context, out AuthenticationHeaderValue authentication)
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
