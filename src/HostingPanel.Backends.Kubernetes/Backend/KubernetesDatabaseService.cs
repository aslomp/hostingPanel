using HostingPanel.Providers.PowerDNS;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HostingPanel.Api.Adapters;
using HostingPanel.Api.Persistence;
using HostingPanel.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace HostingPanel.Api;

public sealed class KubernetesDatabaseService(HostingPanelDbContext db, KubernetesIdentityService identity) : IDatabases, IAccessibleDatabases
{
    public async Task<DatabaseInventory> ListDatabases(AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var principal = await identity.AuthenticateAsync(auth, ct);
        if (principal is null) return new(401, null, "UNAUTHORIZED");
        return new(200, await db.Databases.AsNoTracking().Where(d => d.OwnerId == principal.Id)
            .OrderBy(d => d.Name).Select(d => d.Name).ToListAsync(ct), null);
    }
    Task<DatabaseInventory> IDatabases.ListAsync(AuthenticationHeaderValue auth, CancellationToken ct)
        => ListDatabases(auth, ct);
    public async Task<DatabaseMembers> UsersAsync(string database, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var principal = await identity.AuthenticateAsync(auth, ct);
        if (principal is null) return new(401, null, "UNAUTHORIZED");
        var row = await db.Databases.AsNoTracking().FirstOrDefaultAsync(x => x.Name == database, ct);
        if (row is null || row.OwnerId != principal.Id) return new(404, null, "DATABASE_NOT_FOUND");
        var names = from grant in db.DatabaseGrants.AsNoTracking()
                    join user in db.DatabaseUsers.AsNoTracking() on grant.DatabaseUserId equals user.Id
                    where grant.DatabaseId == row.Id
                    select user.Name;
        return new(200, await names.ToListAsync(ct), null);
    }
    public async Task<DatabaseMutation> CreateAsync(DatabaseCreateRequest input, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var principal = await identity.AuthenticateAsync(auth, ct);
        if (principal is null) return new(401, "UNAUTHORIZED");
        if (!KubernetesInputValidation.ValidDb(input.Database) || !KubernetesInputValidation.ValidDb(input.DbUser) || input.Password.Length < 12)
            return new(400, "INVALID_DATABASE_REQUEST");
        if (await db.Databases.AnyAsync(x => x.Name == input.Database, ct))
            return new(409, "DATABASE_EXISTS");
        var user = await db.DatabaseUsers.FirstOrDefaultAsync(x => x.OwnerId == principal.Id && x.Name == input.DbUser, ct);
        if (user is null)
        {
            user = new DatabaseUser { OwnerId = principal.Id, Name = input.DbUser };
            db.DatabaseUsers.Add(user);
        }
        // DB passwords must be provisioned into a Kubernetes Secret by a separate
        // credential-provider component. Do not persist them in the panel database.
        // Until that provider exists, refuse the mutation instead of claiming success.
        return new(501, "KUBERNETES_CREDENTIAL_PROVISIONING_NOT_IMPLEMENTED");
    }
    async Task<DatabaseMutation> IDatabases.DeleteAsync(string database, string confirmation, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var principal = await identity.AuthenticateAsync(auth, ct);
        if (principal is null) return new(401, "UNAUTHORIZED");
        if (database != confirmation) return new(400, "CONFIRMATION_REQUIRED");
        var row = await db.Databases.FirstOrDefaultAsync(x => x.Name == database, ct);
        if (row is null) return new(404, "DATABASE_NOT_FOUND");
        if (row.OwnerId != principal.Id) return new(403, "ACCESS_DENIED");
        // Deprovisioning a physical database must precede deleting its desired-state record.
        return new(501, "KUBERNETES_DATABASE_DEPROVISIONING_NOT_IMPLEMENTED");
    }

    public async Task<AccessibleDatabaseInventory> ListAccessibleDatabases(AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var principal = await identity.AuthenticateAsync(auth, ct);
        if (principal is null) return new(401, null, null, "UNAUTHORIZED");
        var ids = await identity.VisibleAccountsAsync(principal, ct);
        var entries = await (from d in db.Databases.AsNoTracking()
            join o in db.Accounts.AsNoTracking() on d.OwnerId equals o.Id
            where ids.Contains(o.Id)
            select new { o.Username, d.Name, d.Engine }).ToListAsync(ct);
        var items = entries.Select(x => new OwnedDatabase(x.Username,
            JsonSerializer.SerializeToElement(new { name = x.Name, type = x.Engine }))).ToArray();
        return new(200, items, Array.Empty<DatabaseInventoryWarning>(), null);
    }
    Task<AccessibleDatabaseInventory> IAccessibleDatabases.ListAsync(AuthenticationHeaderValue auth, CancellationToken ct)
        => ListAccessibleDatabases(auth, ct);
}
