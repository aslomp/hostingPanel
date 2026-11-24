using HostingPanel.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HostingPanel.Api;

public static class HostingPanelPersistenceRegistration
{
    public static IServiceCollection AddHostingPanelPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("HostingPanel") ?? "Data Source=hostingpanel.db";
        services.AddDbContext<HostingPanelDbContext>(options => options.UseSqlite(connectionString));
        return services;
    }

    public static async Task InitializeHostingPanelPersistenceAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HostingPanelDbContext>();
        var path = db.Database.GetDbConnection().DataSource;
        if (!string.IsNullOrWhiteSpace(path) && path != ":memory:")
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (directory is not null) Directory.CreateDirectory(directory);
        }
        await db.Database.EnsureCreatedAsync(ct);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
        await db.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout=5000;", ct);
    }
}
