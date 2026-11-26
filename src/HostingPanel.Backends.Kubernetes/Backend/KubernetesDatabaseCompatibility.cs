using System.Net.Http.Headers;
using System.Text.Json;
using HostingPanel.Api.Persistence;
using HostingPanel.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using MySqlConnector;

namespace HostingPanel.Api;

/// <summary>Explicit subset of the pinned DirectAdmin database contract.</summary>
public sealed class KubernetesDatabaseCompatibility(
    HostingPanelDbContext db, KubernetesIdentityService identity,
    IDatabases databases, MariaDbProvisioner provider, KubernetesDatabaseService databaseService)
{
    private static readonly string[] PrivilegeNames = [
        "alter", "alterRoutine", "create", "createRoutine", "createTmpTable", "createView",
        "delete", "drop", "event", "execute", "index", "insert", "lockTables",
        "references", "select", "showView", "trigger", "update"
    ];

    private static Task Error(HttpContext context, int status, string type)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new { type });
    }

    private static Dictionary<string, bool> AllPrivileges() =>
        PrivilegeNames.ToDictionary(p => p, _ => true);

    public async Task ForwardAsync(HttpContext context)
    {
        var ct = context.RequestAborted;
        if (!AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization.ToString(), out var auth))
        {
            await Error(context, 401, "UNAUTHORIZED");
            return;
        }
        var principal = await identity.AuthenticateAsync(auth, ct);
        if (principal is null) { await Error(context, 401, "UNAUTHORIZED"); return; }
        if (!provider.Enabled) { await Error(context, 501, "MARIADB_NOT_CONFIGURED"); return; }
        var path = context.Request.Path.Value ?? "";
        var method = context.Request.Method;
        var database = context.Request.RouteValues["database"]?.ToString();
        var dbuser = context.Request.RouteValues["dbuser"]?.ToString();
        var ownedDatabases = db.Databases.AsNoTracking().Where(x => x.OwnerId == principal.Id);
        var ownedUsers = db.DatabaseUsers.AsNoTracking().Where(x => x.OwnerId == principal.Id);
        try
        {
            if (method == "GET" && path == "/api/db-show/info")
            {
                var info = await provider.InfoAsync(ct);
                await context.Response.WriteAsJsonAsync(new {
                    address = provider.Address, defaultHostPatterns = new[] { "%" },
                    maxDatabaseLength = 64, maxHostsPerUser = 1, maxUsernameLength = 80,
                    oneClickPhpMyAdminLogin = false, sqlMode = info.SqlMode,
                    unixSocket = false, version = info.Version
                }, ct);
                return;
            }
            if (method == "GET" && path == "/api/db-show/databases")
            {
                var noSize = false;
                if (context.Request.Query.TryGetValue("no-size", out var rawNoSize) &&
                    !bool.TryParse(rawNoSize.ToString(), out noSize))
                { await Error(context, 400, "INVALID_DATABASE_REQUEST"); return; }

                var names = await ownedDatabases.OrderBy(x => x.Name).Select(x => x.Name).ToListAsync(ct);
                var result = new List<object>(names.Count);
                foreach (var name in names)
                {
                    var metadata = await provider.MetadataAsync(name, noSize, ct);
                    if (metadata is null) { await Error(context, 500, "DATABASE_CHANGE_UNVERIFIED"); return; }
                    result.Add(new {
                        database = metadata.Database, definerIssues = metadata.DefinerIssues,
                        sizeBytes = metadata.SizeBytes, tableCount = metadata.TableCount,
                        userCount = metadata.UserCount
                    });
                }
                await context.Response.WriteAsJsonAsync(result, ct);
                return;
            }
            if (method == "GET" && database is not null && path == "/api/db-show/databases/" + database)
            {
                if (!await ownedDatabases.AnyAsync(x => x.Name == database, ct))
                { await Error(context, 403, "ACCESS_DENIED"); return; }
                var metadata = await provider.MetadataAsync(database, false, ct);
                if (metadata is null) { await Error(context, 500, "DATABASE_CHANGE_UNVERIFIED"); return; }
                await context.Response.WriteAsJsonAsync(metadata, ct);
                return;
            }
            if (method == "GET" && path == "/api/db-show/users")
            {
                var names = await ownedUsers.OrderBy(x => x.Name).Select(x => x.Name).ToListAsync(ct);
                await context.Response.WriteAsJsonAsync(names.Select(name => new { dbuser = name, hostPatterns = new[] { "%" } }), ct);
                return;
            }
            if (method == "GET" && dbuser is not null && path.EndsWith("/databases", StringComparison.Ordinal))
            {
                var user = await ownedUsers.SingleOrDefaultAsync(x => x.Name == dbuser, ct);
                if (user is null) { await Error(context, 403, "ACCESS_DENIED"); return; }
                var grants = await (from grant in db.DatabaseGrants.AsNoTracking()
                                    join row in ownedDatabases on grant.DatabaseId equals row.Id
                                    where grant.DatabaseUserId == user.Id
                                    select new { row.Name, grant.PrivilegesJson }).ToListAsync(ct);
                if (grants.Any(g => g.PrivilegesJson != "[\"ALL\"]")) { await Error(context, 501, "DATABASE_PRIVILEGES_UNVERIFIED"); return; }
                await context.Response.WriteAsJsonAsync(grants.Select(g => new {
                    database = g.Name, conflictingHosts = false, privileges = AllPrivileges()
                }), ct);
                return;
            }
            if (method == "GET" && dbuser is not null && path.StartsWith("/api/db-show/users/", StringComparison.Ordinal))
            {
                if (!await ownedUsers.AnyAsync(x => x.Name == dbuser, ct)) { await Error(context, 403, "ACCESS_DENIED"); return; }
                await context.Response.WriteAsJsonAsync(new { dbuser, hostPatterns = new[] { "%" } }, ct);
                return;
            }
            if (method == "GET" && database is not null && path.EndsWith("/users", StringComparison.Ordinal))
            {
                var row = await ownedDatabases.SingleOrDefaultAsync(x => x.Name == database, ct);
                if (row is null) { await Error(context, 403, "ACCESS_DENIED"); return; }
                var grants = await (from grant in db.DatabaseGrants.AsNoTracking()
                                    join user in ownedUsers on grant.DatabaseUserId equals user.Id
                                    where grant.DatabaseId == row.Id
                                    select new { user.Name, grant.PrivilegesJson }).ToListAsync(ct);
                if (grants.Any(g => g.PrivilegesJson != "[\"ALL\"]")) { await Error(context, 501, "DATABASE_PRIVILEGES_UNVERIFIED"); return; }
                await context.Response.WriteAsJsonAsync(grants.Select(g => new {
                    dbuser = g.Name, hostPatterns = new[] { "%" }, conflictingHosts = false, privileges = AllPrivileges()
                }), ct);
                return;
            }
            if (method == "POST" && (path == "/api/db-manage/create-db" || path == "/api/db-manage/create-db-with-user"))
            {
                using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: ct);
                var body = document.RootElement;
                if (body.ValueKind != JsonValueKind.Object) { await Error(context, 400, "INVALID_DATABASE_REQUEST"); return; }
                string? Read(string key) => body.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                var withUser = path.EndsWith("/create-db-with-user", StringComparison.Ordinal);
                var fullName = Read("database");
                var fullUser = Read("dbuser");
                if (string.IsNullOrEmpty(fullUser)) fullUser = fullName;
                var prefix = principal.Username + "_";
                if (fullName is null || !fullName.StartsWith(prefix, StringComparison.Ordinal) ||
                    fullName.Length <= prefix.Length || !MariaDbProvisioner.ValidName(fullName, 64) ||
                    (withUser && (fullUser is null || !fullUser.StartsWith(prefix, StringComparison.Ordinal) ||
                    fullUser.Length <= prefix.Length || !MariaDbProvisioner.ValidName(fullUser, 80))))
                { await Error(context, withUser ? 494 : 492, "DATABASE_INVALID_ENTITY_NAME"); return; }
                if (body.TryGetProperty("hostPatterns", out _) || body.TryGetProperty("privileges", out _))
                { await Error(context, 501, "DATABASE_OPTIONS_NOT_IMPLEMENTED"); return; }
                foreach (var key in new[] { "database", "dbuser", "password", "charset", "collation" })
                    if (body.TryGetProperty(key, out var value) && value.ValueKind != JsonValueKind.String)
                    { await Error(context, 400, "INVALID_DATABASE_REQUEST"); return; }
                var password = Read("password");
                if (string.IsNullOrEmpty(password)) password = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
                var result = withUser
                    ? await databaseService.CreateWithUserAsync(new DatabaseCreateRequest(fullName[prefix.Length..], fullUser![prefix.Length..], password), Read("charset"), Read("collation"), auth, ct)
                    : await databaseService.CreateEmptyAsync(fullName[prefix.Length..], Read("charset"), Read("collation"), auth, ct);
                if (result.Error is not null)
                {
                    var status = result.Error switch
                    {
                        "DATABASE_INVALID_CHARSET" => withUser ? 492 : 494,
                        "DATABASE_INVALID_COLLATION" => withUser ? 493 : 495,
                        _ => result.Status == 409 && withUser ? 490 : result.Status == 502 ? 500 : result.Status
                    };
                    await Error(context, status, result.Error);
                    return;
                }
                if (!withUser) { context.Response.StatusCode = 204; return; }
                await context.Response.WriteAsJsonAsync(new {
                    address = provider.Address, database = fullName, dbuser = fullUser,
                    hostPatterns = new[] { "%" }, password, privileges = AllPrivileges(), unixSocket = false
                }, ct);
                return;
            }
            if (method == "DELETE" && database is not null && path == "/api/db-manage/databases/" + database)
            {
                if (context.Request.Query.ContainsKey("drop-orphan-users") && context.Request.Query["drop-orphan-users"] != "false")
                { await Error(context, 501, "ORPHAN_USER_CLEANUP_NOT_IMPLEMENTED"); return; }
                var result = await databases.DeleteAsync(database, database, auth, ct);
                if (result.Error is not null) { await Error(context, result.Status == 404 ? 403 : result.Status == 502 ? 500 : result.Status, result.Error); return; }
                context.Response.StatusCode = 204;
                return;
            }
            await Error(context, 501, "KUBERNETES_OPERATION_NOT_IMPLEMENTED");
        }
        catch (JsonException) { await Error(context, 400, "INVALID_DATABASE_REQUEST"); }
        catch (MySqlException) { await Error(context, 500, "MARIADB_OPERATION_FAILED"); }
        catch (ArgumentException) { await Error(context, 500, "DATABASE_CHANGE_UNVERIFIED"); }
    }
}
