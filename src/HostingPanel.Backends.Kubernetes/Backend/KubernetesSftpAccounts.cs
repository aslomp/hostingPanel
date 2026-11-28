using System.Net.Http.Headers;
using System.Text.Json;
using HostingPanel.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HostingPanel.Api;

internal sealed class SftpAccountRow
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public string Username { get; set; } = "";
    public string Label { get; set; } = "";
    public string PublicKey { get; set; } = "";
    public string RelativePath { get; set; } = "";
}
internal sealed class SftpAccountState
{
    public Guid Id { get; set; }
    public bool Blocked { get; set; }
    public string Observed { get; set; } = "pending";
}
internal sealed class SftpAccountModel : IHostingPanelModelExtension, IHostingPanelSchemaExtension
{
    public string Provider => "Kubernetes.Sftp";
    public IReadOnlyList<HostingPanelSchemaMigration> Migrations => [
        new(1, """CREATE TABLE IF NOT EXISTS "SftpAccounts" ("Id" TEXT NOT NULL PRIMARY KEY, "OwnerId" TEXT NOT NULL, "Username" TEXT NOT NULL UNIQUE, "Label" TEXT NOT NULL, "PublicKey" TEXT NOT NULL, "RelativePath" TEXT NOT NULL DEFAULT '', UNIQUE ("OwnerId", "Label"));"""),
        new(2, """CREATE TABLE IF NOT EXISTS "SftpAccountStates" ("Id" TEXT NOT NULL PRIMARY KEY, "Blocked" INTEGER NOT NULL, "Observed" TEXT NOT NULL);""")
    ];
    public void Configure(ModelBuilder model)
    {
        model.Entity<SftpAccountState>(e => { e.ToTable("SftpAccountStates"); e.HasKey(x => x.Id); });
        model.Entity<SftpAccountRow>(e => {
            e.ToTable("SftpAccounts");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Username).IsUnique();
            e.HasIndex(x => new { x.OwnerId, x.Label }).IsUnique();
        });
    }
}
internal sealed class KubernetesSftpAccounts(HostingPanelDbContext db, KubernetesIdentityService identity, IConfiguration configuration, NfsDirectoryProvisioner storage, KubernetesOperationQueue operations, HostingOperationNotifications notifications) : ISftpAccounts
{
    private bool Enabled => configuration.GetValue<bool>("OpenSsh:Enabled");
    private async Task<long> LimitAsync(HostingAccount owner, CancellationToken ct, string field = "ftp")
    {
        var package = await db.Packages.Where(p => p.Id == owner.PackageId).Select(p => p.LimitsJson).SingleOrDefaultAsync(ct);
        return MailLimitPolicy.Resolve(package, field == "sftpKeys" ? null : owner.LimitsJson, field);
    }
    public async Task<SftpAccountResult> ListAsync(AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var owner = await identity.AuthenticateAsync(auth, ct);
        if (owner is null) return new(401, "UNAUTHORIZED");
        if (!Enabled) return new(501, "SFTP_NOT_CONFIGURED");
        var rows = await db.Set<SftpAccountRow>().AsNoTracking().Where(x => x.OwnerId == owner.Id)
            .OrderBy(x => x.Label).Select(x => new SftpAccountEntry(x.Id, x.Username, x.Label, x.RelativePath, "pending", false, null)).ToListAsync(ct);
        var keys = await db.Set<SftpAccountRow>().AsNoTracking().Where(x => x.OwnerId == owner.Id).Select(x => new { x.Id, x.PublicKey }).ToDictionaryAsync(x => x.Id, x => x.PublicKey, ct);
        var ids = rows.Select(x => x.Id).ToArray();
        var jobs = await db.ReconciliationOperations.AsNoTracking()
            .Where(x => x.ResourceType == "sftp-account" && ids.Contains(x.ResourceId)).ToListAsync(ct);
        var states = await db.Set<SftpAccountState>().AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var observed = rows.Select(row => {
            var pending = jobs.Where(x => x.ResourceId == row.Id && x.Status == "pending").ToArray();
            var state = pending.Any(x => x.LastError != null) ? "failed" : pending.Length != 0 ? "pending"
                : states.GetValueOrDefault(row.Id)?.Observed ?? "pending";
            return row with { ProvisioningStatus = state, Blocked = states.GetValueOrDefault(row.Id)?.Blocked ?? false, PublicKeys = DecodeKeys(keys[row.Id]) };
        }).ToArray();
        return new(200, Accounts: observed, Limit: await LimitAsync(owner, ct), KeyLimit: await LimitAsync(owner, ct, "sftpKeys"));
    }
    internal static bool ValidRelativePath(string? path) => path is not null && path.Length <= 1024 &&
        (path.Length == 0 || path.Split('/').All(part => part.Length is > 0 and <= 255 && part is not ("." or "..") &&
            part.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')));

    internal static bool ValidKey(string key)
    {
        // Accept only a single bare Ed25519 public key, not authorized_keys options.
        if (key.Length > 4096 || key.Any(char.IsControl)) return false;
        var parts = key.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[0] != "ssh-ed25519") return false;
        try
        {
            var blob = Convert.FromBase64String(parts[1]);
            return blob.Length == 51 && blob.AsSpan(0, 19).SequenceEqual(new byte[] {0,0,0,11,115,115,104,45,101,100,50,53,53,49,57,0,0,0,32});
        }
        catch (FormatException) { return false; }
    }
    // The existing column stores a JSON array; legacy single-key rows remain readable.
    internal static string[] DecodeKeys(string stored) =>
        stored.StartsWith('[') ? JsonSerializer.Deserialize<string[]>(stored) ?? throw new InvalidOperationException("Invalid SFTP keys.")
            : [stored];

    internal static string[]? NormalizeKeys(IReadOnlyList<string>? keys)
    {
        if (keys is null || keys.Count < 1 || keys.Any(k => k is null || !ValidKey(k))) return null;
        var normalized = keys.Select(k => k.Trim()).ToArray();
        // Compare key material rather than comments.
        if (normalized.Select(k => k.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]).Distinct(StringComparer.Ordinal).Count() != normalized.Length) return null;
        return normalized;
    }

    public async Task<SftpAccountResult> CreateAsync(SftpAccountRequest input, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var owner = await identity.AuthenticateAsync(auth, ct);
        if (owner is null) return new(401, "UNAUTHORIZED");
        if (!Enabled) return new(501, "SFTP_NOT_CONFIGURED");
        var publicKeys = NormalizeKeys(input.PublicKeys ?? (input.PublicKey is null ? null : new[] { input.PublicKey }));
        if (input.Label is not { Length: > 0 and <= 24 } || !input.Label.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            || publicKeys is null || !ValidRelativePath(input.RelativePath)) return new(400, "INVALID_SFTP_ACCOUNT");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var currentOwner = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == owner.Id && !x.Suspended, ct);
        if (currentOwner is null) return new(403, "ACCESS_DENIED");
        if (publicKeys.LongLength > await LimitAsync(currentOwner, ct, "sftpKeys")) return new(409, "SFTP_KEY_LIMIT_REACHED");
        var limit = await LimitAsync(currentOwner, ct);
        var rows = db.Set<SftpAccountRow>();
        if (await rows.LongCountAsync(x => x.OwnerId == owner.Id, ct) >= limit) return new(409, "SFTP_LIMIT_REACHED");
        try { await storage.ValidateSftpHomeAsync(currentOwner.Username, input.RelativePath, ct); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { return new(409, "SFTP_HOME_UNAVAILABLE_OR_UNSAFE"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return new(503, "SFTP_STORAGE_TIMEOUT"); }
        var id = Guid.NewGuid();
        rows.Add(new SftpAccountRow { Id = id, OwnerId = owner.Id, Username = "sftp-" + id.ToString("N"), Label = input.Label.ToLowerInvariant(), PublicKey = JsonSerializer.Serialize(publicKeys), RelativePath = input.RelativePath });
        operations.Schedule("sftp-account", id, "create");
        try { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); }
        catch (DbUpdateException) { return new(409, "SFTP_ACCOUNT_CONFLICT"); }
        notifications.PublishChange(ReconciliationTarget.Sftp);
        return new(202);
    }
    public async Task<SftpAccountResult> UpdateAsync(Guid id, SftpAccountUpdate input, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var owner = await identity.AuthenticateAsync(auth, ct);
        if (owner is null) return new(401, "UNAUTHORIZED");
        if (!Enabled) return new(501, "SFTP_NOT_CONFIGURED");
        var publicKeys = NormalizeKeys(input.PublicKeys ?? (input.PublicKey is null ? null : new[] { input.PublicKey }));
        if (input.Label is not { Length: > 0 and <= 24 } || !input.Label.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            || !ValidRelativePath(input.RelativePath) || (input.PublicKeys is not null || input.PublicKey is not null) && publicKeys is null)
            return new(400, "INVALID_SFTP_ACCOUNT");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await db.Set<SftpAccountRow>().SingleOrDefaultAsync(x => x.Id == id && x.OwnerId == owner.Id, ct);
        if (row is null) return new(404, "SFTP_ACCOUNT_NOT_FOUND");
        var currentOwner = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == owner.Id && !x.Suspended, ct);
        if (currentOwner is null) return new(403, "ACCESS_DENIED");
        if (publicKeys is not null && publicKeys.LongLength > await LimitAsync(currentOwner, ct, "sftpKeys") && publicKeys.LongLength >= DecodeKeys(row.PublicKey).LongLength)
            return new(409, "SFTP_KEY_LIMIT_REACHED");
        if (!input.Blocked)
        {
            try { await storage.ValidateSftpHomeAsync(owner.Username, input.RelativePath, ct); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            { return new(409, "SFTP_HOME_UNAVAILABLE_OR_UNSAFE"); }
        }
        var state = await db.Set<SftpAccountState>().FindAsync([id], ct);
        if (state is null) { state = new() { Id = id }; db.Add(state); }
        state.Blocked = input.Blocked;
        state.Observed = "pending";
        row.Label = input.Label.ToLowerInvariant();
        row.RelativePath = input.RelativePath;
        if (publicKeys is not null) row.PublicKey = JsonSerializer.Serialize(publicKeys);
        operations.Schedule("sftp-account", id, "update");
        try { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); }
        catch (DbUpdateException) { return new(409, "SFTP_ACCOUNT_CONFLICT"); }
        notifications.PublishChange(ReconciliationTarget.Sftp);
        return new(202);
    }
    public async Task<SftpAccountResult> DeleteAsync(Guid id, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var owner = await identity.AuthenticateAsync(auth, ct);
        if (owner is null) return new(401, "UNAUTHORIZED");
        if (!Enabled) return new(501, "SFTP_NOT_CONFIGURED");
        var row = await db.Set<SftpAccountRow>().SingleOrDefaultAsync(x => x.Id == id && x.OwnerId == owner.Id, ct);
        if (row is null) return new(404, "SFTP_ACCOUNT_NOT_FOUND");
        operations.Schedule("sftp-account", row.Id, "delete");
        var state = await db.Set<SftpAccountState>().FindAsync([id], ct);
        if (state is not null) db.Remove(state);
        db.Remove(row);
        await db.SaveChangesAsync(ct);
        notifications.PublishChange(ReconciliationTarget.Sftp);
        return new(202);
    }
}
