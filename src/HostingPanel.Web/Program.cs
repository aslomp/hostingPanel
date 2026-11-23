using HostingPanel.Web.Components;
using HostingPanel.Web.Services;
using HostingPanel.Web.Realtime;

var builder = WebApplication.CreateBuilder(args);
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
