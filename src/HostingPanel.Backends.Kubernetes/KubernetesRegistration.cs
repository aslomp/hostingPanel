using HostingPanel.Api.Services;
using HostingPanel.Providers.PowerDNS;
using HostingPanel.Providers.CertManager;
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
    settings.PhpImages = [configuration["KubernetesHosting:Php8Image"] ?? "php:8.6.0-fpm"];
    if (configuration.GetValue<bool>("KubernetesHosting:EnablePhp7") && configuration["KubernetesHosting:Php7Image"] is { Length: > 0 } php7)
        settings.PhpImages = [..settings.PhpImages, php7];
    configuration.GetSection("BackendSettings").Bind(settings, options => options.ErrorOnUnknownConfiguration = true);
    var errors = settings.Validator.Validate(settings);
    if (errors.Count != 0)
        throw new InvalidOperationException("Invalid BackendSettings: " + string.Join("; ", errors.Select(x => x.Key + ": " + string.Join(", ", x.Value))));
    services.AddSingleton(settings);
    services.AddSingleton<KubernetesResourceClient>();
    services.AddSingleton<CertificateWork>();
    services.AddScoped<KubernetesIdentityService>();
    services.AddSingleton<ApacheLogModel>();
    services.AddSingleton<IHostingPanelModelExtension>(sp => sp.GetRequiredService<ApacheLogModel>());
    services.AddSingleton<IHostingPanelSchemaExtension>(sp => sp.GetRequiredService<ApacheLogModel>());
    services.AddScoped<IDomainLogs, KubernetesDomainLogs>();
    services.AddHostedService<ApacheLogCollector>();
    services.AddSingleton<MailMutationGate>();
    services.AddScoped<SftpGoClient>();
    services.AddHostedService<SftpGoReconciler>();
    services.AddSingleton<SftpAccountModel>();
    services.AddSingleton<IHostingPanelModelExtension>(sp => sp.GetRequiredService<SftpAccountModel>());
    services.AddSingleton<IHostingPanelSchemaExtension>(sp => sp.GetRequiredService<SftpAccountModel>());
    services.AddScoped<ISftpAccounts, KubernetesSftpAccounts>();
    services.AddHttpClient("sftpgo-management", http => http.Timeout = TimeSpan.FromSeconds(15))
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
    services.AddScoped<StalwartMailProvider>();
    services.AddScoped<StalwartDnsProvisioning>();
    services.AddScoped<IMailProvider>(sp => sp.GetRequiredService<StalwartMailProvider>());
    services.AddScoped<IMailForwarders>(sp => sp.GetRequiredService<StalwartMailProvider>());
    services.AddHttpClient("stalwart-management", http => http.Timeout = TimeSpan.FromSeconds(15))
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
    services.AddScoped<IAuthenticationProvider>(sp => sp.GetRequiredService<KubernetesIdentityService>());
    services.AddScoped<KubernetesSettingsService>();
    services.AddScoped<IAccountNameservers, KubernetesAccountNameservers>();
    services.AddScoped<IKubernetesSettingsReader>(sp => sp.GetRequiredService<KubernetesSettingsService>());
    services.AddScoped<IBackendSettingsAccess>(sp => sp.GetRequiredService<KubernetesSettingsService>());
    services.AddScoped<IHostingNetworkAccess>(sp => sp.GetRequiredService<KubernetesSettingsService>());
    services.AddScoped<KubernetesOperationQueue>();
    services.AddScoped<KubernetesProvisioningService>();
    services.AddScoped<IDomainDnsStaging>(sp => sp.GetRequiredService<KubernetesProvisioningService>());
    services.AddScoped<KubernetesAccountService>();
    services.AddScoped<IAccountPhpSettings, KubernetesAccountPhpSettings>();
    services.AddScoped<IAccountCatalog>(sp => sp.GetRequiredService<KubernetesAccountService>());
    services.AddScoped<KubernetesPackageManagement>();
    services.AddScoped<IPackageManagement>(sp => sp.GetRequiredService<KubernetesPackageManagement>());
    services.AddScoped<IAccountPackages>(sp => sp.GetRequiredService<KubernetesPackageManagement>());
    services.AddScoped<IAccountMutations>(sp => sp.GetRequiredService<KubernetesAccountService>());
    services.AddScoped<IAccountOverview>(sp => sp.GetRequiredService<KubernetesAccountService>());
    services.AddScoped<IUserDetails>(sp => sp.GetRequiredService<KubernetesAccountService>());
    services.AddScoped<IImpersonation>(sp => sp.GetRequiredService<KubernetesAccountService>());
    services.AddSingleton<SubdomainModel>();
    services.AddSingleton<IHostingPanelModelExtension>(sp => sp.GetRequiredService<SubdomainModel>());
    services.AddSingleton<IHostingPanelSchemaExtension>(sp => sp.GetRequiredService<SubdomainModel>());
    services.AddScoped<SubdomainOwnership>();
    services.AddScoped<ISubdomains, KubernetesSubdomainService>();
    services.AddScoped<KubernetesDomainService>();
    services.AddScoped<IDomains>(sp => sp.GetRequiredService<KubernetesDomainService>());
    services.AddScoped<IAccessibleDomains>(sp => sp.GetRequiredService<KubernetesDomainService>());
    services.AddScoped<IDomainPreview>(sp => sp.GetRequiredService<KubernetesDomainService>());
    services.AddScoped<KubernetesWebHosting>();
    services.AddScoped<IWebHosting>(sp => sp.GetRequiredService<KubernetesWebHosting>());
    services.AddSingleton<MariaDbProvisioner>();
    services.AddScoped<KubernetesDatabaseService>();
    services.AddScoped<IDatabases>(sp => sp.GetRequiredService<KubernetesDatabaseService>());
    services.AddScoped<IAccessibleDatabases>(sp => sp.GetRequiredService<KubernetesDatabaseService>());
    services.AddScoped<KubernetesDnsService>();
    services.AddScoped<IDNSProvider>(sp => sp.GetRequiredService<KubernetesDnsService>());
    services.AddScoped<KubernetesFileAccessService>();
    services.AddScoped<IFileAccess>(sp => sp.GetRequiredService<KubernetesFileAccessService>());
    services.AddScoped<KubernetesTicketService>();
    services.AddScoped<ITickets>(sp => sp.GetRequiredService<KubernetesTicketService>());
    services.AddScoped<KubernetesDatabaseCompatibility>();
    services.AddScoped<KubernetesDnsAdminCompatibility>();
    services.AddScoped<KubernetesSubdomainCompatibility>();
    services.AddScoped<KubernetesFtpCompatibility>();
    services.AddScoped<KubernetesCompatibilityHandler>();
    services.AddScoped<IApiCompatibilityHandler>(sp => sp.GetRequiredService<KubernetesCompatibilityHandler>());
    services.AddScoped<KubernetesCertificateProvider>();
    services.AddScoped<ICertificateProvider>(sp => sp.GetRequiredService<KubernetesCertificateProvider>());
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
        services.AddHostedService<StalwartDnsSyncScheduler>();
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

    // Existing installations with no packages also need a usable initial package.
    if (!await db.Packages.AnyAsync())
    {
        db.Packages.Add(new HostingPackage { Name = "default", LimitsJson = "{}" });
        await db.SaveChangesAsync();
    }

    }
}
