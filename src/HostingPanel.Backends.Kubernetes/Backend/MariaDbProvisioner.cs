using MySqlConnector;
using Microsoft.Extensions.Configuration;

namespace HostingPanel.Api;

public sealed class MariaDbConflictException : Exception { }

/// <summary>Physical database operations. The caller must establish account ownership first.</summary>
public sealed class MariaDbProvisioner(IConfiguration configuration)
{
    private readonly SemaphoreSlim gate = new(1);
    private string? ConnectionString => configuration.GetConnectionString("MariaDb");
    public bool Enabled => !string.IsNullOrWhiteSpace(ConnectionString);

    internal static bool ValidName(string name, int max) =>
        name.Length > 0 && name.Length <= max &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    public async Task<IDisposable> LockAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        return new Lease(gate);
    }

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }

    private async Task<MySqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new MySqlConnection(ConnectionString);
        try { await connection.OpenAsync(ct); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }

    private static async Task ExecuteAsync(MySqlConnection connection, string sql, CancellationToken ct, string? password = null)
    {
        using var command = new MySqlCommand(sql, connection);
        if (password is not null) command.Parameters.AddWithValue("@password", password);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task CreateAsync(string name, string username, string password, CancellationToken ct)
    {
        if (!ValidName(name, 64) || !ValidName(username, 80)) throw new ArgumentException("Invalid database identifier");
        await using var connection = await OpenAsync(ct);
        // Check for existing physical resources; never adopt or overwrite external accounts.
        using (var check = new MySqlCommand("SELECT (SELECT COUNT(*) FROM information_schema.SCHEMATA WHERE LOWER(SCHEMA_NAME)=LOWER(@db)) + (SELECT COUNT(*) FROM mysql.user WHERE LOWER(User)=LOWER(@user))", connection))
        {
            check.Parameters.AddWithValue("@db", name);
            check.Parameters.AddWithValue("@user", username);
            if (Convert.ToInt64(await check.ExecuteScalarAsync(ct)) != 0) throw new MariaDbConflictException();
        }
        // DDL auto-commits. Track only acknowledged creates for compensation.
        bool databaseCreated = false, userCreated = false;
        try
        {
            await ExecuteAsync(connection, $"CREATE DATABASE `{name}`", ct);
            databaseCreated = true;
            await ExecuteAsync(connection, $"CREATE USER '{username}'@'%' IDENTIFIED BY @password", ct, password);
            userCreated = true;
            // Escape underscores in database-level GRANT patterns (MariaDB wildcard semantics).
            var grantName = name.Replace("_", "\\_");
            await ExecuteAsync(connection, $"GRANT ALL PRIVILEGES ON `{grantName}`.* TO '{username}'@'%'", ct);
        }
        catch
        {
            try
            {
                if (userCreated) await ExecuteAsync(connection, $"DROP USER '{username}'@'%'", CancellationToken.None);
                if (databaseCreated) await ExecuteAsync(connection, $"DROP DATABASE `{name}`", CancellationToken.None);
            }
            catch (MySqlException) { /* Preserve original failure; operator must reconcile uncertain state. */ }
            throw;
        }
    }

    public async Task DeleteAsync(string name, IReadOnlyList<string> ownedUsers, CancellationToken ct)
    {
        if (!ValidName(name, 64)) throw new ArgumentException("Invalid database identifier");
        await using var connection = await OpenAsync(ct);
        // Remove database-level grants before dropping the schema; orphan users remain managed.
        var grantName = name.Replace("_", "\\_");
        var users = new List<(string User, string Host)>();
        using (var command = new MySqlCommand("SELECT User, Host FROM mysql.db WHERE Db = @db", connection))
        {
            command.Parameters.AddWithValue("@db", grantName);
            using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) users.Add((reader.GetString(0), reader.GetString(1)));
        }
        foreach (var account in users)
        {
            if (!ValidName(account.User, 80) || account.Host != "%" || !ownedUsers.Contains(account.User, StringComparer.Ordinal))
                throw new InvalidOperationException("Unexpected database grant; manual reconciliation required.");
            await ExecuteAsync(connection, $"REVOKE ALL PRIVILEGES ON `{grantName}`.* FROM '{account.User}'@'%'", ct);
        }
        await ExecuteAsync(connection, $"DROP DATABASE IF EXISTS `{name}`", ct);
    }
}
