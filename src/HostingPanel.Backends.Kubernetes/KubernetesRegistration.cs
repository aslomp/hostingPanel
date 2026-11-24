using HostingPanel.Api.Persistence;
using Microsoft.EntityFrameworkCore;
namespace HostingPanel.Api;
public static class KubernetesRegistration
{
    public static IServiceCollection AddKubernetesBackend(this IServiceCollection services, IConfiguration configuration)
    {

    var settings = new KubernetesBackendSettings();
    configuration.GetSection("BackendSettings").Bind(settings, options => options.ErrorOnUnknownConfiguration = true);
    var errors = settings.Validator.Validate(settings);
    if (errors.Count != 0)
        throw new InvalidOperationException("Invalid BackendSettings: " + string.Join("; ", errors.Select(x => x.Key + ": " + string.Join(", ", x.Value))));
    services.AddSingleton(settings);
    services.AddScoped<IHostingBackend, KubernetesBackend>();
    services.AddSingleton<PhpIdentityModel>();
    services.AddSingleton<IHostingPanelModelExtension>(sp => sp.GetRequiredService<PhpIdentityModel>());
    services.AddSingleton<IHostingPanelSchemaExtension>(sp => sp.GetRequiredService<PhpIdentityModel>());
    services.AddSingleton<NfsDirectoryProvisioner>();
    services.AddHostedService<WebsiteReconciler>();

        return services;
    }

    public static async Task InitializeKubernetesBackendAsync(this IServiceProvider services, IConfiguration configuration)
    {

    using var scope = services.CreateScope();
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
    // Explicit one-time bootstrap; never create a default or blank administrator.
    // Configure through environment variables or another protected secret source.
    var bootstrapUser = configuration["HostingPanel:Bootstrap:Username"];
    var bootstrapPassword = configuration["HostingPanel:Bootstrap:Password"];
    if (!await db.Accounts.AnyAsync())
    {
        if (bootstrapUser is not null || bootstrapPassword is not null)
        {
            if (string.IsNullOrWhiteSpace(bootstrapUser) || bootstrapUser.Length > 64
                || !bootstrapUser.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')
                || string.IsNullOrEmpty(bootstrapPassword) || bootstrapPassword.Length < 12)
                throw new InvalidOperationException("Kubernetes bootstrap requires a valid username and a password of at least 12 characters.");
            db.Accounts.Add(new HostingAccount {
                Username = bootstrapUser, Kind = "admin",
                PasswordHash = PasswordVerifier.Hash(bootstrapPassword)
            });
            await db.SaveChangesAsync();
        }
    }

    }
}
