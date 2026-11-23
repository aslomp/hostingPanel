using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using HostingPanel.Api.Adapters;
using HostingPanel.Api.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddScoped<LegacyUserDetailsService>();
builder.Services.AddScoped<LegacyAccountOverviewService>();
builder.Services.AddScoped<DomainManagementService>();
builder.Services.AddScoped<AccountCatalogService>();
builder.Services.AddScoped<AccessibleDomainsService>();
builder.Services.AddScoped<ImpersonationService>();
builder.Services.AddScoped<DnsZoneService>();
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

// Modern DirectAdmin contract: GET /api/search/users returns a string array.
// DirectAdmin itself decides which user, reseller and admin account lists are
// accessible. HostingPanel does not duplicate its scope or privilege model.
app.MapGet("/api/search/users", async (HttpContext context, AccountCatalogService catalog) =>
{
    if (!TryGetBasic(context, out var authentication))
        return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);
    var query = context.Request.Query["q"].ToString();
    var rawLimit = context.Request.Query["limit"].ToString();
    var limit = 50;
    if (rawLimit.Length > 0 && (!int.TryParse(rawLimit, out limit) || limit is < 1 or > 1000))
        return Results.Json(new { type = "BAD_REQUEST" }, statusCode: 400);
    if (query.Length > 256)
        return Results.Json(new { type = "BAD_REQUEST" }, statusCode: 400);

    var listing = await catalog.ListAsync(authentication, context.RequestAborted);
    return listing.Accounts is null
        ? Results.Json(new { type = listing.Error ?? "UPSTREAM_ERROR" }, statusCode: listing.Status)
        : Results.Json(listing.Accounts.Select(x => x.Username)
            .Where(x => string.IsNullOrEmpty(query) ||
                x.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Take(limit).ToArray());
});

// HostingPanel-specific view used to show the DirectAdmin creator relationship.
// Creator and role are metadata, not a second authorization model.
app.MapGet("/hosting/accounts", async (HttpContext context, AccountCatalogService catalog) =>
{
    if (!TryGetBasic(context, out var auth))
        return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);
    var result = await catalog.ListAsync(auth, context.RequestAborted);
    return result.Accounts is null
        ? Results.Json(new { type = result.Error ?? "UPSTREAM_ERROR" }, statusCode: result.Status)
        : Results.Json(new { accounts = result.Accounts });
});

app.MapGet("/hosting/domains/accessible", async (HttpContext context,
    AccessibleDomainsService service) =>
{
    if (!TryGetBasic(context, out var auth))
        return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);
    var result = await service.ListAsync(auth, context.RequestAborted);
    return result.Items is null
        ? Results.Json(new { type = result.Warning ?? "UPSTREAM_ERROR" }, statusCode: result.Status)
        : Results.Json(new {
            items = result.Items, domains = result.Items.Select(x => x.Domain).ToArray(),
            warning = result.Warning
        });
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

// Read-only validation of an administrator's delegated DirectAdmin identity.
// This does not create a session, change privileges or disclose credentials.
app.MapGet("/hosting/impersonation/check", async (HttpContext context,
    ImpersonationService service) =>
{
    if (!TryGetBasic(context, out var auth))
        return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);
    var target = context.Request.Query["user"].ToString();
    var result = await service.CheckAsync(auth, target, context.RequestAborted);
    return result.Error is null
        ? Results.Json(new { allowed = true, effectiveUsername = target })
        : Results.Json(new { type = result.Error }, statusCode: result.Status);
});

// HostingPanel business view: available legacy account properties and the assigned package.
// This is separate from the strict modern /api/users/{username}/config Swagger response.
app.MapGet("/hosting/users/{username}/overview", async (HttpContext context,
    string username, LegacyAccountOverviewService service) =>
{
    if (!TryGetBasic(context, out var auth))
        return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);
    if (!LegacyAccountOverviewService.ValidUsername(username))
        return Results.Json(new { type = "INVALID_USERNAME" }, statusCode: 400);
    var result = await service.GetAsync(username, auth, context.RequestAborted);
    return result.Config is null
        ? Results.Json(new { type = result.Error ?? "UPSTREAM_ERROR" }, statusCode: result.Status)
        : Results.Json(new {
            username, config = result.Config,
            package = result.Package, packageSettings = result.PackageSettings,
            packageError = result.Error, usage = result.Usage, usageError = result.UsageError
        });
});

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

// Read and update a single domain's DirectAdmin settings. Not part of the pinned
// modern DirectAdmin Swagger: these are HostingPanel-owned business routes.
app.MapGet("/hosting/domains/{domain}", async (HttpContext context,
    string domain, DomainManagementService service) =>
{
    if (!TryGetBasic(context, out var auth))
        return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);
    var result = await service.GetDetailsAsync(domain, auth, context.RequestAborted);
    return result.Settings is not null
        ? Results.Json(new { domain, settings = result.Settings })
        : Results.Json(new { type = result.Error ?? "UPSTREAM_ERROR" }, statusCode: result.Status);
});

app.MapPut("/hosting/domains/{domain}/settings", async (HttpContext context, string domain,
    DomainSettingRequest input, DomainManagementService service) =>
{
    if (!TryGetBasic(context, out var auth))
        return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);
    var result = await service.UpdateSettingAsync(domain, input.Setting, input.Value,
        auth, context.RequestAborted);
    return result.Error is null
        ? Results.Json(new { domain, setting = input.Setting, updated = true })
        : Results.Json(new { type = result.Error }, statusCode: result.Status);
});

// Administrator-level server-wide DNS zones, independent of hosting account ownership.
// DirectAdmin itself verifies the original admin account on each request.
app.MapGet("/hosting/dns/zones", async (HttpContext context, DnsZoneService service) =>
{
    if (!TryGetBasic(context, out var auth))
        return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);
    var rawPage = context.Request.Query["page"].ToString();
    if (!int.TryParse(string.IsNullOrEmpty(rawPage) ? "1" : rawPage, out var page)
        || page is < 1 or > 10000)
        return Results.Json(new { type = "INVALID_PAGE" }, statusCode: 400);
    var result = await service.ListAdminZonesAsync(auth, page, context.RequestAborted);
    return result.Zones is null
        ? Results.Json(new { type = result.Error ?? "UPSTREAM_ERROR" }, statusCode: result.Status)
        : Results.Json(new { zones = result.Zones, page });
});

// DNS management is a HostingPanel-owned interface because the pinned modern
// DirectAdmin Swagger has no public DNS zone CRUD path.
app.MapGet("/hosting/domains/{domain}/dns", async (HttpContext context,
    string domain, DnsZoneService service) =>
{
    if (!TryGetBasic(context, out var auth))
        return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);
    var result = await service.ReadAsync(domain, auth, context.RequestAborted);
    return result.Records is null
        ? Results.Json(new { type = result.Error ?? "UPSTREAM_ERROR" }, statusCode: result.Status)
        : Results.Json(new { domain, records = result.Records });
});

app.MapPost("/hosting/domains/{domain}/dns", async (HttpContext context, string domain,
    DnsChange input, DnsZoneService service) =>
{
    if (!TryGetBasic(context, out var auth))
        return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);
    var result = await service.ChangeAsync(domain, input, auth, context.RequestAborted);
    return result.Error is null
        ? Results.Json(new { domain, action = input.Action, applied = true })
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
