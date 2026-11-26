using MySqlConnector;
using Microsoft.Extensions.Configuration;

namespace HostingPanel.Api;

public sealed record MariaDbMetadata(string Database, string DefaultCharset, string DefaultCollation,
    long SizeBytes, long TableCount, long ViewCount, long RoutineCount, long EventCount,
    long TriggerCount, long UserCount, long DefinerIssues);

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

    public async Task<MariaDbMetadata?> MetadataAsync(string name, bool noSize, CancellationToken ct)
    {
        RequireName(name, 64);
        await using var connection = await OpenAsync(ct);
        // All schema names are values in these catalog queries, not SQL identifiers.
        // Count objects whose DEFINER no longer exists as a MariaDB account.
        const string sql = """
            SELECT s.SCHEMA_NAME, s.DEFAULT_CHARACTER_SET_NAME, s.DEFAULT_COLLATION_NAME,
                (SELECT COALESCE(SUM(COALESCE(t.DATA_LENGTH,0) + COALESCE(t.INDEX_LENGTH,0)),0)
                 FROM information_schema.TABLES t WHERE t.TABLE_SCHEMA = s.SCHEMA_NAME AND t.TABLE_TYPE = 'BASE TABLE' AND @includeSize = 1),
                (SELECT COUNT(*) FROM information_schema.TABLES t WHERE t.TABLE_SCHEMA = s.SCHEMA_NAME AND t.TABLE_TYPE = 'BASE TABLE'),
                (SELECT COUNT(*) FROM information_schema.VIEWS v WHERE v.TABLE_SCHEMA = s.SCHEMA_NAME),
                (SELECT COUNT(*) FROM information_schema.ROUTINES r WHERE r.ROUTINE_SCHEMA = s.SCHEMA_NAME),
                (SELECT COUNT(*) FROM information_schema.EVENTS e WHERE e.EVENT_SCHEMA = s.SCHEMA_NAME),
                (SELECT COUNT(*) FROM information_schema.TRIGGERS t WHERE t.TRIGGER_SCHEMA = s.SCHEMA_NAME),
                (SELECT COUNT(DISTINCT d.User) FROM mysql.db d WHERE d.Db = @grantPattern),
                (SELECT COUNT(*) FROM (
                    SELECT TABLE_SCHEMA AS schema_name, DEFINER FROM information_schema.VIEWS
                    UNION ALL SELECT ROUTINE_SCHEMA, DEFINER FROM information_schema.ROUTINES
                    UNION ALL SELECT EVENT_SCHEMA, DEFINER FROM information_schema.EVENTS
                    UNION ALL SELECT TRIGGER_SCHEMA, DEFINER FROM information_schema.TRIGGERS
                ) AS objects WHERE objects.schema_name = @name AND NOT EXISTS (
                    SELECT 1 FROM mysql.user u WHERE CONCAT(u.User, '@', u.Host) = objects.DEFINER
                ))
            FROM information_schema.SCHEMATA s WHERE s.SCHEMA_NAME = @name
            """;
        using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@name", name);
        command.Parameters.AddWithValue("@grantPattern", GrantPattern(name));
        command.Parameters.AddWithValue("@includeSize", !noSize);
        using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new MariaDbMetadata(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            Convert.ToInt64(reader.GetValue(3)), Convert.ToInt64(reader.GetValue(4)),
            Convert.ToInt64(reader.GetValue(5)), Convert.ToInt64(reader.GetValue(6)),
            Convert.ToInt64(reader.GetValue(7)), Convert.ToInt64(reader.GetValue(8)),
            Convert.ToInt64(reader.GetValue(9)), Convert.ToInt64(reader.GetValue(10)));
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

    public async Task CreateUserAsync(string username, string password, CancellationToken ct)
    {
        var account = QuoteAccount(username);
        await using var connection = await OpenAsync(ct);
        using var check = new MySqlCommand("SELECT COUNT(*) FROM mysql.user WHERE LOWER(User) = LOWER(@user)", connection);
        check.Parameters.AddWithValue("@user", username);
        if (Convert.ToInt64(await check.ExecuteScalarAsync(ct)) != 0) throw new MariaDbConflictException();
        await ExecuteAsync(connection, $"CREATE USER {account} IDENTIFIED BY @password", ct, password);
    }

    public async Task ChangePasswordAsync(string username, string password, CancellationToken ct)
    {
        var account = QuoteAccount(username);
        await using var connection = await OpenAsync(ct);
        await VerifyAccountAsync(connection, username, false, ct);
        await ExecuteAsync(connection, $"ALTER USER {account} IDENTIFIED BY @password", ct, password);
    }

    public async Task DeleteUserAsync(string username, CancellationToken ct)
    {
        var account = QuoteAccount(username);
        await using var connection = await OpenAsync(ct);
        if (!await VerifyAccountAsync(connection, username, true, ct)) return;
        await ExecuteAsync(connection, $"DROP USER {account}", ct);
    }

    private static async Task<bool> VerifyAccountAsync(MySqlConnection connection, string username, bool allowMissing, CancellationToken ct)
    {
        using var command = new MySqlCommand("SELECT User, Host FROM mysql.user WHERE LOWER(User) = LOWER(@user)", connection);
        command.Parameters.AddWithValue("@user", username);
        using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            if (allowMissing) return false;
            throw new InvalidOperationException("Managed database account is missing.");
        }
        if (reader.GetString(0) != username || reader.GetString(1) != "%" || await reader.ReadAsync(ct))
            throw new InvalidOperationException("Unexpected database account; manual reconciliation required.");
        return true;
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
