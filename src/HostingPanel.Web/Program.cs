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
    options.DefaultRequestCulture = new RequestCulture("nl-NL");
    options.SupportedCultures = cultures;
    options.SupportedUICultures = cultures;
    options.RequestCultureProviders =
    [
        new CookieRequestCultureProvider(),
        new AcceptLanguageHeaderRequestCultureProvider()
    ];
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
app.UseAntiforgery();
app.MapHub<TicketHub>("/hubs/tickets");
app.MapRazorComponents<App>()
   .AddInteractiveServerRenderMode()
   .WithStaticAssets();
app.Run();
