using HostingPanel.Web.Components;
using HostingPanel.Web.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddHttpClient("hosting-api", client =>
{
    var raw = builder.Configuration["HostingPanel:ApiBaseUrl"]
        ?? throw new InvalidOperationException("HostingPanel:ApiBaseUrl is required.");
    client.BaseAddress = new Uri(raw, UriKind.Absolute);
});
builder.Services.AddScoped<DirectAdminSession>();

var app = builder.Build();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error");
    app.UseHsts();
}
app.UseHttpsRedirection();

// .NET 10 static web assets are endpoint-mapped. Without MapStaticAssets,
// the framework boot script may return HTTP 404 and interactive components never start.
app.MapStaticAssets();
app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<App>()
   .AddInteractiveServerRenderMode()
   .WithStaticAssets();
app.Run();
