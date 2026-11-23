using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using HostingPanel.Api.Adapters;
using HostingPanel.Api.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddScoped<LegacyUserDetailsService>();
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
