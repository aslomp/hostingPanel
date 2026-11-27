using System.Net;
using HostingPanel.Api.Services;
namespace HostingPanel.Api;
public static class DirectAdminRegistration
{
    public static IServiceCollection AddDirectAdminBackend(this IServiceCollection services, IConfiguration configuration)
    {

    services.AddScoped<DirectAdminBackend>();
    services.AddScoped<ISubdomains, DirectAdminSubdomains>();
    services.AddScoped<IMailProvider, DirectAdminMailProvider>();
    services.AddScoped<IAccountNameservers, DirectAdminAccountNameservers>();
    services.AddScoped<IAuthenticationProvider>(sp => sp.GetRequiredService<DirectAdminBackend>());
    services.AddScoped<IApiCompatibilityHandler>(sp => sp.GetRequiredService<DirectAdminBackend>());
    services.AddScoped<IAccessibleDatabases>(sp => sp.GetRequiredService<DirectAdminBackend>().AccessibleDatabases);
    services.AddScoped<IDatabases>(sp => sp.GetRequiredService<DirectAdminBackend>().Databases);
    services.AddScoped<IDomains>(sp => sp.GetRequiredService<DirectAdminBackend>().Domains);
    services.AddScoped<IFileAccess>(sp => sp.GetRequiredService<DirectAdminBackend>().FileAccess);
    services.AddScoped<ITickets>(sp => sp.GetRequiredService<DirectAdminBackend>().Tickets);
    services.AddScoped<IAccountCatalog>(sp => sp.GetRequiredService<DirectAdminBackend>().AccountCatalog);
    services.AddScoped<DirectAdminAccountPackages>();
    services.AddScoped<IPackageManagement>(sp => sp.GetRequiredService<DirectAdminAccountPackages>());
    services.AddScoped<IAccountPackages>(sp => sp.GetRequiredService<DirectAdminAccountPackages>());
    services.AddScoped<IAccountMutations>(sp => sp.GetRequiredService<DirectAdminBackend>().AccountMutations);
    services.AddScoped<IImpersonation>(sp => sp.GetRequiredService<DirectAdminBackend>().Impersonation);
    services.AddScoped<IAccountOverview>(sp => sp.GetRequiredService<DirectAdminBackend>().AccountOverview);
    services.AddScoped<IUserDetails>(sp => sp.GetRequiredService<DirectAdminBackend>().UserDetails);
    services.AddScoped<IWebHosting>(sp => sp.GetRequiredService<DirectAdminBackend>().WebHosting);
    services.AddScoped<ICertificateProvider>(sp => sp.GetRequiredService<DirectAdminBackend>());
    services.AddScoped<IDomainPreview>(sp => sp.GetRequiredService<DirectAdminBackend>());
    services.AddScoped<IDNSProvider>(sp => sp.GetRequiredService<DirectAdminBackend>());
    services.AddScoped<LegacyUserDetailsService>();
    services.AddScoped<LegacyAccountOverviewService>();
    services.AddScoped<DomainManagementService>();
    services.AddScoped<AccountCatalogService>();
    services.AddScoped<AccountMutationService>();
    services.AddScoped<AccessibleDomainsService>();
    services.AddScoped<IAccessibleDomains>(sp => sp.GetRequiredService<AccessibleDomainsService>());
    services.AddScoped<ImpersonationService>();
    services.AddScoped<DnsZoneService>();
    services.AddScoped<DatabaseManagementService>();
    services.AddScoped<AccessibleDatabasesService>();
    services.AddScoped<FileAccessService>();
    services.AddScoped<TicketService>();
    services.AddHttpClient("legacy-directadmin", client =>
    {
        var configured = configuration["DirectAdmin:BaseUrl"]
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

        return services;
    }
}
