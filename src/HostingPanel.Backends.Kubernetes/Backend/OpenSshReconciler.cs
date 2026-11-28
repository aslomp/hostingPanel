using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HostingPanel.Api.Persistence;
using HostingPanel.Providers.CertManager;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HostingPanel.Api;

// SQLite remains authoritative. Kubernetes performs the subPath bind mounts;
// the shared sshd needs no CAP_SYS_ADMIN and has no management HTTP endpoint.
internal sealed class OpenSshReconciler(IServiceScopeFactory scopes, IConfiguration configuration,
    HostingOperationNotifications notifications, KubernetesResourceClient resources,
    NfsDirectoryProvisioner storage, ILogger<OpenSshReconciler> logger) : BackgroundService
{
    private bool initial = true;
    private string Namespace => configuration["KubernetesHosting:Namespace"] ?? "hosting";
    private string Workload => configuration["OpenSsh:Workload"] ?? throw new InvalidOperationException("Missing OpenSSH workload.");

    protected override Task ExecuteAsync(CancellationToken ct)
    {
        if (!configuration.GetValue<bool>("OpenSsh:Enabled")) return Task.CompletedTask;
        return ReconciliationLoop.RunAsync(notifications.Reader(ReconciliationTarget.Sftp),
            (changed, token) => resources.WatchCustomResourcesAsync("apps", "v1", Namespace, "deployments",
                "hostingpanel/sftp-workload=" + Workload, changed, _ => changed(), token),
            ReconcileAsync, logger, Timeout.InfiniteTimeSpan, ct, signal: () => notifications.Signal(ReconciliationTarget.Sftp));
    }

    internal sealed record Login(string LoginName, int Uid, int Gid, string[] Keys, string SubPath, Guid OwnerId, Guid Id);
    internal static string Snapshot(IEnumerable<Login> logins) => JsonSerializer.Serialize(logins.OrderBy(x => x.LoginName, StringComparer.Ordinal)
        .Select(x => new { login = x.LoginName, uid = x.Uid, gid = x.Gid, keys = x.Keys }));
    internal static string Revision(string snapshot) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot))).ToLowerInvariant();
    // Include mount roots in the revision even when the keys and numeric identity do not change.
    internal static string SnapshotWithPaths(IEnumerable<Login> logins) => JsonSerializer.Serialize(logins.OrderBy(x => x.LoginName, StringComparer.Ordinal)
        .Select(x => new { login = x.LoginName, uid = x.Uid, gid = x.Gid, keys = x.Keys, path = x.SubPath }));

    private async Task<bool> ReconcileAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HostingPanelDbContext>();
        var queue = scope.ServiceProvider.GetRequiredService<KubernetesOperationQueue>();
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var triggers = await db.ReconciliationOperations.Where(x => x.Status == "pending" &&
                (x.ResourceType == "sftp-owner" || x.ResourceType == "sftp-package")).ToListAsync(ct);
            var owners = triggers.Where(x => x.ResourceType == "sftp-owner").Select(x => x.ResourceId).ToHashSet();
            var packages = triggers.Where(x => x.ResourceType == "sftp-package").Select(x => x.ResourceId).ToArray();
            if (packages.Length != 0) owners.UnionWith(await db.Accounts.Where(x => x.PackageId.HasValue && packages.Contains(x.PackageId.Value)).Select(x => x.Id).ToListAsync(ct));
            var targets = await db.Set<SftpAccountRow>().Where(x => initial || owners.Contains(x.OwnerId)).Select(x => x.Id).ToListAsync(ct);
            var queued = await db.ReconciliationOperations.Where(x => x.Status == "pending" && x.ResourceType == "sftp-account").Select(x => x.ResourceId).ToListAsync(ct);
            foreach (var id in targets.Except(queued)) queue.Schedule("sftp-account", id, "update");
            foreach (var trigger in triggers) Complete(trigger);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            initial = false;
        }
        var jobs = await db.ReconciliationOperations.Where(x => x.ResourceType == "sftp-account" && x.Status == "pending").ToListAsync(ct);
        var rows = await db.Set<SftpAccountRow>().AsNoTracking().ToListAsync(ct);
        var ownerIds = rows.Select(x => x.OwnerId).Distinct().ToArray();
        var ownersById = await db.Accounts.AsNoTracking().Where(x => ownerIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var packageIds = ownersById.Values.Where(x => x.PackageId.HasValue).Select(x => x.PackageId!.Value).Distinct().ToArray();
        var packageById = await db.Packages.AsNoTracking().Where(x => packageIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var states = await db.Set<SftpAccountState>().ToDictionaryAsync(x => x.Id, ct);
        var writes = configuration.GetValue<bool>("OpenSsh:EnableWrites");
        var logins = new List<Login>();
        var failed = new HashSet<Guid>();
        foreach (var row in rows)
        {
            try
            {
                if (!writes || !ownersById.TryGetValue(row.OwnerId, out var owner) || owner.Suspended || states.GetValueOrDefault(row.Id)?.Blocked == true ||
                    MailLimitPolicy.Resolve(owner.PackageId is { } pid ? packageById.GetValueOrDefault(pid)?.LimitsJson : null, owner.LimitsJson, "ftp") == 0) continue;
                if (row.Username != "sftp-" + row.Id.ToString("N") || !KubernetesInputValidation.ValidUser(owner.Username) ||
                    !KubernetesSftpAccounts.ValidRelativePath(row.RelativePath)) throw new InvalidOperationException("Invalid SFTP identity.");
                var keys = KubernetesSftpAccounts.NormalizeKeys(KubernetesSftpAccounts.DecodeKeys(row.PublicKey)) ?? throw new InvalidOperationException("Invalid keys.");
                var (uid,gid) = await storage.ValidateSftpHomeAsync(owner.Username, row.RelativePath, ct);
                if (uid < 1000 || gid < 1000 || uid is 65534 or 1654 or 1001 || gid is 65534 or 1654 or 1001)
                    throw new InvalidOperationException("Reserved Unix identity.");
                logins.Add(new(row.Username,uid,gid,keys,owner.Username + (row.RelativePath.Length == 0 ? "" : "/" + row.RelativePath),owner.Id,row.Id));
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                failed.Add(row.Id);
                logger.LogWarning("SFTP storage preflight failed for {Id}: {ErrorType}", row.Id, ex.GetType().Name);
            }
        }
        foreach (var group in logins.GroupBy(x => x.Uid).Where(x => x.Select(y => y.OwnerId).Distinct().Count() > 1).ToArray())
            foreach (var login in group) failed.Add(login.Id);
        logins.RemoveAll(x => failed.Contains(x.Id));
        try
        {
            var snapshot = SnapshotWithPaths(logins);
            if (Encoding.UTF8.GetByteCount(snapshot) > 900_000) throw new InvalidOperationException("SFTP snapshot exceeds ConfigMap budget.");
            if (!await resources.ActivateOpenSshAsync(Namespace,Workload,Revision(snapshot),snapshot,
                logins.ToDictionary(x => x.LoginName,x => x.SubPath),ct)) return true;
            var active = logins.Select(x => x.Id).ToHashSet();
            foreach (var row in rows)
            {
                if (!states.TryGetValue(row.Id, out var state)) { state = new() { Id = row.Id }; db.Add(state); }
                state.Observed = failed.Contains(row.Id) ? "failed" : active.Contains(row.Id) ? "active" : "disabled";
            }
            foreach (var job in jobs)
                if (failed.Contains(job.ResourceId)) { job.Attempts++; job.LastError = "SFTP_STORAGE_UNAVAILABLE_OR_UNSAFE"; }
                else Complete(job);
            foreach (var state in states.Values.Where(x => rows.All(r => r.Id != x.Id))) db.Remove(state);
            await db.SaveChangesAsync(ct);
            if (jobs.Count > 0 || failed.Count > 0) notifications.Publish();
            return failed.Count > 0;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            foreach (var job in jobs) { job.Attempts++; job.LastError = "SFTP_RECONCILIATION_FAILED"; }
            await db.SaveChangesAsync(ct);
            if (jobs.Count > 0) notifications.Publish();
            logger.LogWarning(ex, "OpenSSH snapshot activation failed");
            return true;
        }
    }

    private static void Complete(ReconciliationOperation operation)
    {
        operation.Status = "completed";
        operation.CompletedAt = DateTimeOffset.UtcNow;
        operation.LastError = null;
    }
}
