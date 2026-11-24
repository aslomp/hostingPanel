using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using HostingPanel.Api;
using HostingPanel.Api.Adapters;
using HostingPanel.Api.Services;
using HostingPanel.Api.Persistence;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
var provider = builder.Configuration["HostingPanel:Backend"] ?? "DirectAdmin";
if (provider.Equals("Kubernetes", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddDbContext<HostingPanelDbContext>(options =>
        options.UseSqlite(builder.Configuration.GetConnectionString("HostingPanel")
            ?? "Data Source=hostingpanel.db"));
    builder.Services.AddScoped<IHostingBackend, KubernetesBackend>();
}
else if (provider.Equals("DirectAdmin", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddScoped<IHostingBackend, DirectAdminBackend>();
else
    throw new InvalidOperationException($"Unknown HostingPanel backend '{provider}'.");
builder.Services.AddScoped<LegacyUserDetailsService>();
builder.Services.AddScoped<LegacyAccountOverviewService>();
builder.Services.AddScoped<DomainManagementService>();
builder.Services.AddScoped<AccountCatalogService>();
builder.Services.AddScoped<AccountMutationService>();
builder.Services.AddScoped<AccessibleDomainsService>();
builder.Services.AddScoped<ImpersonationService>();
builder.Services.AddScoped<DnsZoneService>();
builder.Services.AddScoped<DatabaseManagementService>();
builder.Services.AddScoped<AccessibleDatabasesService>();
builder.Services.AddScoped<FileAccessService>();
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
if (provider.Equals("Kubernetes", StringComparison.OrdinalIgnoreCase))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<HostingPanelDbContext>();
    var path = db.Database.GetDbConnection().DataSource;
    if (!string.IsNullOrWhiteSpace(path) && path != ":memory:")
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is not null) Directory.CreateDirectory(directory);
    }
    await db.Database.EnsureCreatedAsync();
    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
    await db.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout=5000;");
}

app.UseSwaggerUI(options =>
{
    options.RoutePrefix = "swagger";
    options.SwaggerEndpoint("/openapi/directadmin.json", "DirectAdmin API — full pinned contract");
    options.DocumentTitle = "HostingPanel | DirectAdmin API documentation";
    options.DefaultModelsExpandDepth(-1);
});
app.UseHttpsRedirection();

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
