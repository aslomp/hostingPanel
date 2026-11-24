using HostingPanel.Api;
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddHostingPanelPersistence(builder.Configuration);
var provider = builder.Configuration["HostingPanel:Backend"] ?? "DirectAdmin";
if (provider.Equals("Kubernetes", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddKubernetesBackend(builder.Configuration);
else if (provider.Equals("DirectAdmin", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddDirectAdminBackend(builder.Configuration);
else
    throw new InvalidOperationException($"Unknown HostingPanel backend '{provider}'.");
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
