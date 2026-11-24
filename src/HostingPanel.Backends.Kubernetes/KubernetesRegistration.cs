using HostingPanel.Providers.PowerDNS;
using HostingPanel.Api.Persistence;
using Microsoft.EntityFrameworkCore;
namespace HostingPanel.Api;
public static class KubernetesRegistration
{
    public static IServiceCollection AddKubernetesBackend(this IServiceCollection services, IConfiguration configuration)
    {

    var settings = new KubernetesBackendSettings();
    settings.DnsAutoCreateZones = configuration.GetValue("PowerDns:AutoCreateZones", true);
    settings.DnsWebIp = configuration["PowerDns:WebIp"] ?? "";
    settings.DnsNs1 = configuration["PowerDns:Ns1"] ?? "";
    settings.DnsNs2 = configuration["PowerDns:Ns2"] ?? "";
    configuration.GetSection("BackendSettings").Bind(settings, options => options.ErrorOnUnknownConfiguration = true);
    var errors = settings.Validator.Validate(settings);
    if (errors.Count != 0)
        throw new InvalidOperationException("Invalid BackendSettings: " + string.Join("; ", errors.Select(x => x.Key + ": " + string.Join(", ", x.Value))));
    services.AddSingleton(settings);
    services.AddScoped<KubernetesBackend>();
    services.AddScoped<KubernetesSettingsService>();
    services.AddScoped<IKubernetesSettingsReader>(sp => sp.GetRequiredService<KubernetesSettingsService>());
    services.AddScoped<IDomainDnsStaging>(sp => sp.GetRequiredService<KubernetesBackend>());
    services.AddScoped<KubernetesIdentityService>();
    services.AddScoped<IAuthenticationProvider>(sp => sp.GetRequiredService<KubernetesIdentityService>());
    services.AddScoped<IApiCompatibilityHandler>(sp => sp.GetRequiredService<KubernetesBackend>());
    services.AddScoped<IAccessibleDatabases>(sp => sp.GetRequiredService<KubernetesBackend>().AccessibleDatabases);
    services.AddScoped<IDatabases>(sp => sp.GetRequiredService<KubernetesBackend>().Databases);
    services.AddScoped<IAccessibleDomains>(sp => sp.GetRequiredService<KubernetesBackend>().AccessibleDomains);
    services.AddScoped<IDomains>(sp => sp.GetRequiredService<KubernetesBackend>().Domains);
    services.AddScoped<IFileAccess>(sp => sp.GetRequiredService<KubernetesBackend>().FileAccess);
    services.AddScoped<ITickets>(sp => sp.GetRequiredService<KubernetesBackend>().Tickets);
    services.AddScoped<IAccountCatalog>(sp => sp.GetRequiredService<KubernetesBackend>().AccountCatalog);
    services.AddScoped<IAccountMutations>(sp => sp.GetRequiredService<KubernetesBackend>().AccountMutations);
    services.AddScoped<IImpersonation>(sp => sp.GetRequiredService<KubernetesBackend>().Impersonation);
    services.AddScoped<IAccountOverview>(sp => sp.GetRequiredService<KubernetesBackend>().AccountOverview);
    services.AddScoped<IUserDetails>(sp => sp.GetRequiredService<KubernetesBackend>().UserDetails);
    services.AddScoped<IWebHosting>(sp => sp.GetRequiredService<KubernetesBackend>().WebHosting);
    services.AddScoped<ICertificateProvider>(sp => sp.GetRequiredService<KubernetesBackend>());
    services.AddScoped<IDomainPreview>(sp => sp.GetRequiredService<KubernetesBackend>());
    services.AddScoped<IDNSProvider>(sp => sp.GetRequiredService<KubernetesBackend>());
    services.AddScoped<IBackendSettingsAccess>(sp => sp.GetRequiredService<KubernetesSettingsService>());
    services.AddScoped<IHostingNetworkAccess>(sp => sp.GetRequiredService<KubernetesSettingsService>());
    services.AddSingleton<PhpIdentityModel>();
    services.AddSingleton<IHostingPanelModelExtension>(sp => sp.GetRequiredService<PhpIdentityModel>());
    services.AddSingleton<IHostingPanelSchemaExtension>(sp => sp.GetRequiredService<PhpIdentityModel>());
    services.AddSingleton<NfsDirectoryProvisioner>();
    services.AddHostedService<WebsiteReconciler>();
    services.AddHostedService<CertificateReconciler>();
    if (configuration.GetValue<bool>("PowerDns:Enabled"))
    {
        services.AddHttpClient<PowerDnsClient>(http =>
        {
            var endpoint = configuration["PowerDns:BaseUrl"] ?? throw new InvalidOperationException("Missing PowerDns:BaseUrl");
            var key = configuration["PowerDns:ApiKey"] ?? throw new InvalidOperationException("Missing PowerDns:ApiKey");
            http.BaseAddress = new Uri(endpoint.TrimEnd('/') + "/api/v1/servers/" + Uri.EscapeDataString(configuration["PowerDns:ServerId"] ?? "localhost") + "/");
            http.DefaultRequestHeaders.Add("X-API-Key", key);
            http.Timeout = TimeSpan.FromSeconds(15);
        });
        services.AddHostedService<DnsReconciler>();
    }

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
