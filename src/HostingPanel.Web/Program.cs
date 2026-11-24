using MudBlazor.Services;
using HostingPanel.Api;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using HostingPanel.Web.Components;
using HostingPanel.Web.Services;
using HostingPanel.Web.Realtime;
using Microsoft.AspNetCore.Localization;
using System.Globalization;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddSingleton<HostingOperationNotifications>();
builder.Services.AddHostingPanelPersistence(builder.Configuration);
var provider = builder.Configuration["HostingPanel:Backend"] ?? "DirectAdmin";
if (provider.Equals("Kubernetes", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddKubernetesBackend(builder.Configuration);
else if (provider.Equals("DirectAdmin", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddDirectAdminBackend(builder.Configuration);
else
    throw new InvalidOperationException($"Unknown HostingPanel backend '{provider}'.");
builder.Services.AddScoped<IAuthenticationProvider>(sp => sp.GetRequiredService<IHostingBackend>());
builder.Services.AddScoped<IApiCompatibilityHandler>(sp => sp.GetRequiredService<IHostingBackend>());
builder.Services.AddScoped<IAccessibleDatabases>(sp => sp.GetRequiredService<IHostingBackend>().AccessibleDatabases);
builder.Services.AddScoped<IDatabases>(sp => sp.GetRequiredService<IHostingBackend>().Databases);
builder.Services.AddScoped<IAccessibleDomains>(sp => sp.GetRequiredService<IHostingBackend>().AccessibleDomains);
builder.Services.AddScoped<IDomains>(sp => sp.GetRequiredService<IHostingBackend>().Domains);
builder.Services.AddScoped<IFileAccess>(sp => sp.GetRequiredService<IHostingBackend>().FileAccess);
builder.Services.AddScoped<ITickets>(sp => sp.GetRequiredService<IHostingBackend>().Tickets);
builder.Services.AddScoped<IAccountCatalog>(sp => sp.GetRequiredService<IHostingBackend>().AccountCatalog);
builder.Services.AddScoped<IAccountMutations>(sp => sp.GetRequiredService<IHostingBackend>().AccountMutations);
builder.Services.AddScoped<IImpersonation>(sp => sp.GetRequiredService<IHostingBackend>().Impersonation);
builder.Services.AddScoped<IAccountOverview>(sp => sp.GetRequiredService<IHostingBackend>().AccountOverview);
builder.Services.AddScoped<IWebHosting>(sp => sp.GetRequiredService<IHostingBackend>().WebHosting);
builder.Services.AddScoped<ICertificateProvider>(sp => sp.GetRequiredService<IHostingBackend>());
builder.Services.AddScoped<IDomainPreview>(sp => sp.GetRequiredService<IHostingBackend>());
builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    var cultures = new[] { new CultureInfo("nl-NL"), new CultureInfo("en-US") };
    options.DefaultRequestCulture = new RequestCulture("en-US");
    options.SupportedCultures = cultures;
    options.SupportedUICultures = cultures;
    options.RequestCultureProviders =
    [
        new CookieRequestCultureProvider(),
        new AcceptLanguageHeaderRequestCultureProvider()
    ];
});
builder.Services.AddSingleton<PanelSessionStore>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.Cookie.Name = "__Host-HostingPanel";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.Path = "/";
        options.SlidingExpiration = false;
        options.Events.OnValidatePrincipal = context =>
        {
            var id = context.Principal?.FindFirst(PanelSessionStore.SessionClaim)?.Value;
            if (context.HttpContext.RequestServices.GetRequiredService<PanelSessionStore>().Get(id) is null)
                context.RejectPrincipal();
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToLogin = context =>
        {
            if (!HttpMethods.IsGet(context.Request.Method) || context.Request.Path.StartsWithSegments("/_blazor"))
                context.Response.StatusCode = 401;
            else context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();
// HTTPS deployments issue secure antiforgery cookies; permit loopback HTTP smoke tests.
builder.Services.AddAntiforgery(options => options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddPolicy("login", context => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
        { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddMudServices();
// Transitional same-host adapter until all UI operations use typed backend contracts.
builder.Services.AddHttpClient("hosting-api", (services, client) =>
{
    var server = services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>();
    var addresses = server.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?.Addresses;
    var configured = addresses?.FirstOrDefault(x => x.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        ?? addresses?.FirstOrDefault()
        ?? throw new InvalidOperationException("HostingPanel has no listening address.");
    var uri = new Uri(configured.Replace("://+", "://localhost").Replace("://*", "://localhost"));
    var host = uri.Host is "0.0.0.0" or "[::]" or "::" ? "localhost" : uri.Host;
    client.BaseAddress = new UriBuilder(uri.Scheme, host, uri.Port).Uri;
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    AllowAutoRedirect = false,
    UseCookies = false
});
builder.Services.AddSingleton<TicketChannelRegistry>();
builder.Services.AddScoped<DirectAdminSession>();
builder.Services.AddScoped<NavigationContextState>();
builder.Services.AddScoped<TicketLiveMonitor>();

// Support dotnet run in a non-Development environment without requiring dotnet publish.
// This reads the generated static web asset manifest for framework and wwwroot assets.
builder.WebHost.UseStaticWebAssets();

var app = builder.Build();
await app.Services.InitializeHostingPanelPersistenceAsync();
if (provider.Equals("Kubernetes", StringComparison.OrdinalIgnoreCase))
    await app.Services.InitializeKubernetesBackendAsync(app.Configuration);

app.UseSwaggerUI(options =>
{
    options.RoutePrefix = "swagger";
    options.SwaggerEndpoint("/openapi/directadmin.json", "DirectAdmin API — full pinned contract");
    options.DocumentTitle = "HostingPanel | DirectAdmin API documentation";
    options.DefaultModelsExpandDepth(-1);
});

app.UseRequestLocalization();
app.MapGet("/culture/set", (HttpContext context, string culture, string? returnUrl) =>
{
    if (culture is not ("nl-NL" or "en-US")) return Results.BadRequest("Unsupported culture.");
    context.Response.Cookies.Append(CookieRequestCultureProvider.DefaultCookieName,
        CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
        new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), IsEssential = true,
            HttpOnly = true, Secure = context.Request.IsHttps, SameSite = SameSiteMode.Lax });
    var destination = !string.IsNullOrWhiteSpace(returnUrl) && returnUrl.StartsWith('/')
        && !returnUrl.StartsWith("//") && !returnUrl.Contains('\\') ? returnUrl : "/";
    return Results.LocalRedirect(destination);
});
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error");
    app.UseHsts();
}
app.UseHttpsRedirection();

// Serve framework and application assets through the static web asset endpoints.
app.MapStaticAssets();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseAntiforgery();
app.MapPanelAuthentication();
app.MapHub<TicketHub>("/hubs/tickets");
app.MapRazorComponents<App>()
   .AddInteractiveServerRenderMode()
   .WithStaticAssets()
   .RequireAuthorization();
// Published upstream Swagger 2.0 contract is authoritative, including schemas and security definitions.
app.MapGet("/openapi/directadmin.json", (IWebHostEnvironment environment) =>
{
    var filename = Path.Combine(environment.ContentRootPath, "spec", "directadmin.swagger.json");
    return File.Exists(filename)
        ? Results.File(filename, "application/json")
        : Results.Problem("Pinned OpenAPI specification unavailable.", statusCode: 503);
});
app.MapGet("/swagger/v1/swagger.json", (IWebHostEnvironment environment) =>
{
    var filename = Path.Combine(environment.ContentRootPath, "spec", "directadmin.swagger.json");
    return File.Exists(filename)
        ? Results.File(filename, "application/json")
        : Results.Problem("Pinned OpenAPI specification unavailable.", statusCode: 503);
});
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
app.MapControllers();
app.Run();
