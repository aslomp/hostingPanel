using System.Net.Http.Headers;
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
internal sealed class SftpAccountModel : IHostingPanelModelExtension, IHostingPanelSchemaExtension
{
    public string Provider => "Kubernetes.Sftp";
    public IReadOnlyList<HostingPanelSchemaMigration> Migrations => [
        new(1, """CREATE TABLE IF NOT EXISTS "SftpAccounts" ("Id" TEXT NOT NULL PRIMARY KEY, "OwnerId" TEXT NOT NULL, "Username" TEXT NOT NULL UNIQUE, "Label" TEXT NOT NULL, "PublicKey" TEXT NOT NULL, UNIQUE ("OwnerId", "Label"));"""),
        new(2, """ALTER TABLE "SftpAccounts" ADD COLUMN "RelativePath" TEXT NOT NULL DEFAULT '';""")
    ];
    public void Configure(ModelBuilder model)
    {
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
    private bool Enabled => !string.IsNullOrWhiteSpace(configuration["SftpGo:BaseUrl"]);
    private async Task<long> LimitAsync(HostingAccount owner, CancellationToken ct)
    {
        var package = await db.Packages.Where(p => p.Id == owner.PackageId).Select(p => p.LimitsJson).SingleOrDefaultAsync(ct);
        return MailLimitPolicy.Resolve(package, owner.LimitsJson, "ftp");
    }
    public async Task<SftpAccountResult> ListAsync(AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var owner = await identity.AuthenticateAsync(auth, ct);
        if (owner is null) return new(401, "UNAUTHORIZED");
        if (!Enabled) return new(501, "SFTP_NOT_CONFIGURED");
        var rows = await db.Set<SftpAccountRow>().AsNoTracking().Where(x => x.OwnerId == owner.Id)
            .OrderBy(x => x.Label).Select(x => new SftpAccountEntry(x.Id, x.Username, x.Label, x.RelativePath)).ToListAsync(ct);
        var ids = rows.Select(x => x.Id).ToArray();
        var jobs = await db.ReconciliationOperations.AsNoTracking()
            .Where(x => x.ResourceType == "sftp-account" && ids.Contains(x.ResourceId)).ToListAsync(ct);
        var observed = rows.Select(row => {
            var pending = jobs.Where(x => x.ResourceId == row.Id && x.Status == "pending").ToArray();
            var state = pending.Any(x => x.LastError != null) ? "failed" : pending.Length != 0 ? "pending"
                : jobs.Any(x => x.ResourceId == row.Id && x.Status == "completed") ? "disabled" : "pending";
            return row with { ProvisioningStatus = state };
        }).ToArray();
        return new(200, Accounts: observed, Limit: await LimitAsync(owner, ct));
    }
    internal static bool ValidRelativePath(string? path) => path is not null && path.Length <= 1024 &&
        (path.Length == 0 || path.Split('/').All(part => part.Length is > 0 and <= 255 && part is not ("." or "..") &&
            part.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')));

    private static bool ValidKey(string key)
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
    public async Task<SftpAccountResult> CreateAsync(SftpAccountRequest input, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var owner = await identity.AuthenticateAsync(auth, ct);
        if (owner is null) return new(401, "UNAUTHORIZED");
        if (!Enabled) return new(501, "SFTP_NOT_CONFIGURED");
        if (input.Label is not { Length: > 0 and <= 24 } || !input.Label.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            || input.PublicKey is null || !ValidKey(input.PublicKey) || !ValidRelativePath(input.RelativePath)) return new(400, "INVALID_SFTP_ACCOUNT");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var currentOwner = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == owner.Id && !x.Suspended, ct);
        if (currentOwner is null) return new(403, "ACCESS_DENIED");
        var limit = await LimitAsync(currentOwner, ct);
        var rows = db.Set<SftpAccountRow>();
        if (await rows.LongCountAsync(x => x.OwnerId == owner.Id, ct) >= limit) return new(409, "SFTP_LIMIT_REACHED");
        try { await storage.ValidateSftpHomeAsync(currentOwner.Username, input.RelativePath, ct); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { return new(409, "SFTP_HOME_UNAVAILABLE_OR_UNSAFE"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return new(503, "SFTP_STORAGE_TIMEOUT"); }
        var id = Guid.NewGuid();
        rows.Add(new SftpAccountRow { Id = id, OwnerId = owner.Id, Username = "sftp-" + id.ToString("N"), Label = input.Label.ToLowerInvariant(), PublicKey = input.PublicKey.Trim(), RelativePath = input.RelativePath });
        operations.Schedule("sftp-account", id, "create");
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
        db.Remove(row);
        await db.SaveChangesAsync(ct);
        notifications.PublishChange(ReconciliationTarget.Sftp);
        return new(202);
    }
}
