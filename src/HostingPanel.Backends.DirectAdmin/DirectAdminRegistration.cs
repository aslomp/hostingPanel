using System.Net;
using HostingPanel.Api.Services;
namespace HostingPanel.Api;
public static class DirectAdminRegistration
{
    public static IServiceCollection AddDirectAdminBackend(this IServiceCollection services, IConfiguration configuration)
    {

    services.AddScoped<IHostingBackend, DirectAdminBackend>();
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
