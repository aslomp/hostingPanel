using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using HostingPanel.Web.Components;
using HostingPanel.Web.Services;
using HostingPanel.Web.Realtime;
using Microsoft.AspNetCore.Localization;
using System.Globalization;

var builder = WebApplication.CreateBuilder(args);
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
builder.Services.AddHttpClient("hosting-api", client =>
{
    var raw = builder.Configuration["HostingPanel:ApiBaseUrl"]
        ?? throw new InvalidOperationException("HostingPanel:ApiBaseUrl is required.");
    client.BaseAddress = new Uri(raw, UriKind.Absolute);
});
builder.Services.AddSingleton<TicketChannelRegistry>();
builder.Services.AddScoped<DirectAdminSession>();
builder.Services.AddScoped<NavigationContextState>();
builder.Services.AddScoped<TicketLiveMonitor>();

// Support dotnet run in a non-Development environment without requiring dotnet publish.
// This reads the generated static web asset manifest for framework and wwwroot assets.
builder.WebHost.UseStaticWebAssets();

var app = builder.Build();
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
app.Run();
