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

public sealed class KubernetesDatabaseService(HostingPanelDbContext db, KubernetesIdentityService identity, MariaDbProvisioner? provisioner = null) : IDatabases, IAccessibleDatabases
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
        if (!KubernetesInputValidation.ValidDb(input.Database) || !KubernetesInputValidation.ValidDb(input.DbUser) ||
            string.IsNullOrEmpty(input.Password) || input.Password.Length is < 12 or > 128)
            return new(400, "INVALID_DATABASE_REQUEST");
        var name = principal.Username + "_" + input.Database;
        var username = principal.Username + "_" + input.DbUser;
        if (!MariaDbProvisioner.ValidName(name, 64) || !MariaDbProvisioner.ValidName(username, 80))
            return new(400, "INVALID_DATABASE_REQUEST");
        if (provisioner is null || !provisioner.Enabled) return new(501, "MARIADB_NOT_CONFIGURED");
        using var lease = await provisioner.LockAsync(ct);
        if (await db.Databases.AnyAsync(x => x.Name.ToLower() == name.ToLower(), ct) ||
            await db.DatabaseUsers.AnyAsync(x => x.Name.ToLower() == username.ToLower(), ct))
            return new(409, "DATABASE_EXISTS");
        var row = new HostedDatabase { OwnerId = principal.Id, Name = name };
        var user = new DatabaseUser { OwnerId = principal.Id, Name = username };
        try
        {
            await provisioner.CreateAsync(name, username, input.Password, ct);
            db.Databases.Add(row);
            db.DatabaseUsers.Add(user);
            db.DatabaseGrants.Add(new DatabaseGrant { DatabaseId = row.Id, DatabaseUserId = user.Id, PrivilegesJson = "[\"ALL\"]" });
            await db.SaveChangesAsync(ct);
            return new(200, null);
        }
        catch (MariaDbConflictException) { return new(409, "DATABASE_EXISTS"); }
        catch (MySqlConnector.MySqlException) { return new(502, "MARIADB_OPERATION_FAILED"); }
        catch (DbUpdateException) { return new(502, "DATABASE_CHANGE_UNVERIFIED"); }
    }
    async Task<DatabaseMutation> IDatabases.DeleteAsync(string database, string confirmation, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var principal = await identity.AuthenticateAsync(auth, ct);
        if (principal is null) return new(401, "UNAUTHORIZED");
        if (database != confirmation) return new(400, "CONFIRMATION_REQUIRED");
        var row = await db.Databases.FirstOrDefaultAsync(x => x.Name == database, ct);
        if (row is null) return new(404, "DATABASE_NOT_FOUND");
        if (row.OwnerId != principal.Id) return new(403, "ACCESS_DENIED");
        // Never act on legacy/unverified inventory or on names outside the owner's prefix.
        if (!MariaDbProvisioner.ValidName(row.Name, 64) ||
            !row.Name.StartsWith(principal.Username + "_", StringComparison.Ordinal))
            return new(409, "DATABASE_OWNERSHIP_UNVERIFIED");
        if (provisioner is null || !provisioner.Enabled) return new(501, "MARIADB_NOT_CONFIGURED");
        using var lease = await provisioner.LockAsync(ct);
        try
        {
            var ownedUsers = await (from grant in db.DatabaseGrants
                                    join user in db.DatabaseUsers on grant.DatabaseUserId equals user.Id
                                    where grant.DatabaseId == row.Id && user.OwnerId == principal.Id
                                    select user.Name).ToListAsync(ct);
            await provisioner.DeleteAsync(row.Name, ownedUsers, ct);
            db.Databases.Remove(row);
            await db.SaveChangesAsync(ct);
            return new(200, null);
        }
        catch (MySqlConnector.MySqlException) { return new(502, "MARIADB_OPERATION_FAILED"); }
        catch (DbUpdateException) { return new(502, "DATABASE_CHANGE_UNVERIFIED"); }
        catch (InvalidOperationException) { return new(409, "DATABASE_OWNERSHIP_UNVERIFIED"); }
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
