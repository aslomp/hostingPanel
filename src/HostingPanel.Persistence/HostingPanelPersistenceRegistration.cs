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

        // Keep existing Kubernetes installations compatible. Migration tracking below
        // is independent from the provider model and never imports DirectAdmin data.
        await db.Database.EnsureCreatedAsync(ct);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
        await db.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout=5000;", ct);
        await db.Database.ExecuteSqlRawAsync(
            """CREATE TABLE IF NOT EXISTS "BackendSchemaMigrations" ("Provider" TEXT NOT NULL, "Version" INTEGER NOT NULL, PRIMARY KEY ("Provider", "Version"));""", ct);

        foreach (var extension in scope.ServiceProvider.GetServices<IHostingPanelSchemaExtension>())
        {
            if (string.IsNullOrWhiteSpace(extension.Provider))
                throw new InvalidOperationException("Schema extension provider must not be empty.");
            var migrations = extension.Migrations.OrderBy(x => x.Version).ToArray();
            if (migrations.Any(x => x.Version < 1 || string.IsNullOrWhiteSpace(x.Sql))
                || migrations.Select(x => x.Version).Distinct().Count() != migrations.Length)
                throw new InvalidOperationException($"Invalid schema migrations for {extension.Provider}.");

            foreach (var migration in migrations)
            {
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                var applied = await db.Database.SqlQueryRaw<int>(
                    """SELECT COUNT(*) AS "Value" FROM "BackendSchemaMigrations" WHERE "Provider" = {0} AND "Version" = {1}""",
                    extension.Provider, migration.Version).SingleAsync(ct);
                if (applied == 0)
                {
                    await db.Database.ExecuteSqlRawAsync(migration.Sql, ct);
                    await db.Database.ExecuteSqlRawAsync(
                        """INSERT INTO "BackendSchemaMigrations" ("Provider", "Version") VALUES ({0}, {1})""",
                        new object[] { extension.Provider, migration.Version }, ct);
                }
                await tx.CommitAsync(ct);
            }
        }
    }
}
