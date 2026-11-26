using MySqlConnector;
using Microsoft.Extensions.Configuration;

namespace HostingPanel.Api;

public sealed class MariaDbConflictException : Exception { }
public sealed class MariaDbEncodingException(string code) : Exception(code) { public string Code { get; } = code; }

/// <summary>Physical database operations. The caller must establish account ownership first.</summary>
public sealed class MariaDbProvisioner(IConfiguration configuration)
{
    private readonly SemaphoreSlim gate = new(1);
    private string? ConnectionString => configuration.GetConnectionString("MariaDb");
    public bool Enabled => !string.IsNullOrWhiteSpace(ConnectionString);

    internal static bool ValidName(string name, int max) =>
        name.Length > 0 && name.Length <= max &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    private static string RequireName(string name, int max)
    {
        if (!ValidName(name, max))
            throw new ArgumentException("Invalid MariaDB identifier.", nameof(name));
        return name;
    }

    private static string QuoteDatabase(string name) =>
        "`" + RequireName(name, 64) + "`";

    private static string GrantPattern(string name) =>
        RequireName(name, 64).Replace("_", "\\_", StringComparison.Ordinal);

    private static string QuoteGrantDatabase(string name) =>
        "`" + GrantPattern(name) + "`";

    private static string QuoteAccount(string name) =>
        "'" + RequireName(name, 80) + "'@'%'";

    public string Address
    {
        get
        {
            var options = new MySqlConnectionStringBuilder(ConnectionString);
            return options.Server + ":" + options.Port;
        }
    }

    public async Task<(string Version, string[] SqlMode)> InfoAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        using var command = new MySqlCommand("SELECT VERSION(), @@SESSION.sql_mode", connection);
        using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("Missing server metadata.");
        return (reader.GetString(0), reader.GetString(1).Split(',', StringSplitOptions.RemoveEmptyEntries));
    }

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

    public async Task CreateAsync(string name, string? username, string? password, CancellationToken ct, string? charset = null, string? collation = null)
    {
        var databaseIdentifier = QuoteDatabase(name);
        var accountIdentifier = username is null ? null : QuoteAccount(username);
        var grantIdentifier = QuoteGrantDatabase(name);
        await using var connection = await OpenAsync(ct);
        // Check for existing physical resources; never adopt or overwrite external accounts.
        using (var check = new MySqlCommand("SELECT (SELECT COUNT(*) FROM information_schema.SCHEMATA WHERE LOWER(SCHEMA_NAME)=LOWER(@db)) + (SELECT COUNT(*) FROM mysql.user WHERE LOWER(User)=LOWER(@user))", connection))
        {
            check.Parameters.AddWithValue("@db", name);
            check.Parameters.AddWithValue("@user", (object?)username ?? DBNull.Value);
            if (Convert.ToInt64(await check.ExecuteScalarAsync(ct)) != 0) throw new MariaDbConflictException();
        }
        var encoding = await EncodingClauseAsync(connection, charset, collation, ct);
        // DDL auto-commits. Track only acknowledged creates for compensation.
        bool databaseCreated = false, userCreated = false;
        try
        {
            await ExecuteAsync(connection, $"CREATE DATABASE {databaseIdentifier}{encoding}", ct);
            databaseCreated = true;
            if (accountIdentifier is null) return;
            await ExecuteAsync(connection, $"CREATE USER {accountIdentifier} IDENTIFIED BY @password", ct, password);
            userCreated = true;
            // Escape underscores in database-level GRANT patterns (MariaDB wildcard semantics).
            await ExecuteAsync(connection, $"GRANT ALL PRIVILEGES ON {grantIdentifier}.* TO {accountIdentifier}", ct);
        }
        catch
        {
            try
            {
                if (userCreated) await ExecuteAsync(connection, $"DROP USER {accountIdentifier}", CancellationToken.None);
                if (databaseCreated) await ExecuteAsync(connection, $"DROP DATABASE {databaseIdentifier}", CancellationToken.None);
            }
            catch (MySqlException) { /* Preserve original failure; operator must reconcile uncertain state. */ }
            throw;
        }
    }

    private static async Task<string> EncodingClauseAsync(MySqlConnection connection, string? charset, string? collation, CancellationToken ct)
    {
        var clause = "";
        if (!string.IsNullOrEmpty(charset))
        {
            if (!ValidName(charset, 64)) throw new MariaDbEncodingException("DATABASE_INVALID_CHARSET");
            using var command = new MySqlCommand("SELECT CHARACTER_SET_NAME FROM information_schema.CHARACTER_SETS WHERE CHARACTER_SET_NAME = @charset", connection);
            command.Parameters.AddWithValue("@charset", charset);
            if (await command.ExecuteScalarAsync(ct) is not string actual)
                throw new MariaDbEncodingException("DATABASE_INVALID_CHARSET");
            clause = " CHARACTER SET " + QuoteDatabase(actual);
        }
        if (!string.IsNullOrEmpty(collation))
        {
            if (!ValidName(collation, 64)) throw new MariaDbEncodingException("DATABASE_INVALID_COLLATION");
            using var command = new MySqlCommand("SELECT COLLATION_NAME FROM information_schema.COLLATIONS WHERE COLLATION_NAME = @collation AND (@charset IS NULL OR CHARACTER_SET_NAME = @charset)", connection);
            command.Parameters.AddWithValue("@collation", collation);
            command.Parameters.AddWithValue("@charset", string.IsNullOrEmpty(charset) ? (object)DBNull.Value : charset);
            if (await command.ExecuteScalarAsync(ct) is not string actual)
                throw new MariaDbEncodingException("DATABASE_INVALID_COLLATION");
            clause += " COLLATE " + QuoteDatabase(actual);
        }
        return clause;
    }

    public async Task DeleteAsync(string name, IReadOnlyList<string> ownedUsers, CancellationToken ct)
    {
        var databaseIdentifier = QuoteDatabase(name);
        var grantIdentifier = QuoteGrantDatabase(name);
        await using var connection = await OpenAsync(ct);
        // Remove database-level grants before dropping the schema; orphan users remain managed.
        var grantName = GrantPattern(name);
        var users = new List<(string User, string Host)>();
        using (var command = new MySqlCommand("SELECT User, Host FROM mysql.db WHERE Db = @db", connection))
        {
            command.Parameters.AddWithValue("@db", grantName);
            using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) users.Add((reader.GetString(0), reader.GetString(1)));
        }
        // Validate every grant before issuing any mutation.
        if (users.Any(account => !ValidName(account.User, 80) || account.Host != "%" ||
            !ownedUsers.Contains(account.User, StringComparer.Ordinal)))
            throw new InvalidOperationException("Unexpected database grant; manual reconciliation required.");

        foreach (var account in users)
            await ExecuteAsync(connection, $"REVOKE ALL PRIVILEGES ON {grantIdentifier}.* FROM {QuoteAccount(account.User)}", ct);
        await ExecuteAsync(connection, $"DROP DATABASE IF EXISTS {databaseIdentifier}", ct);
    }
}
