using HostingPanel.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HostingPanel.Api;

internal sealed class SftpGoReconciler(IServiceScopeFactory scopes, IConfiguration configuration,
    HostingOperationNotifications notifications, ILogger<SftpGoReconciler> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(configuration["SftpGo:BaseUrl"])) return Task.CompletedTask;
        return ReconciliationLoop.RunAsync(notifications.Reader(ReconciliationTarget.Sftp),
            ReconcileAsync, TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan, logger, ct);
    }

    private bool initial = true;

    private async Task<bool> ReconcileAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HostingPanelDbContext>();
        var queue = scope.ServiceProvider.GetRequiredService<KubernetesOperationQueue>();
        // Expand durable owner/package invalidations atomically into per-account jobs.
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var triggers = await db.ReconciliationOperations.Where(x => x.Status == "pending" &&
                (x.ResourceType == "sftp-owner" || x.ResourceType == "sftp-package")).ToListAsync(ct);
            var owners = triggers.Where(x => x.ResourceType == "sftp-owner").Select(x => x.ResourceId).ToHashSet();
            var packages = triggers.Where(x => x.ResourceType == "sftp-package").Select(x => x.ResourceId).ToHashSet();
            if (packages.Count != 0)
                owners.UnionWith(await db.Accounts.Where(x => x.PackageId.HasValue && packages.Contains(x.PackageId.Value))
                    .Select(x => x.Id).ToListAsync(ct));
            var targets = await db.Set<SftpAccountRow>().Where(x => initial || owners.Contains(x.OwnerId))
                .Select(x => x.Id).ToListAsync(ct);
            var queued = await db.ReconciliationOperations.Where(x => x.ResourceType == "sftp-account" && x.Status == "pending")
                .Select(x => x.ResourceId).ToListAsync(ct);
            foreach (var id in targets.Except(queued)) queue.Schedule("sftp-account", id, "update");
            foreach (var trigger in triggers) Complete(trigger);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            initial = false;
        }
        var pending = await db.ReconciliationOperations.AsNoTracking().Where(x => x.ResourceType == "sftp-account" && x.Status == "pending")
            .Select(x => x.ResourceId).Distinct().ToListAsync(ct);
        var retry = false;
        foreach (var id in pending)
        {
            using var workScope = scopes.CreateScope();
            var workDb = workScope.ServiceProvider.GetRequiredService<HostingPanelDbContext>();
            var client = workScope.ServiceProvider.GetRequiredService<SftpGoClient>();
            var jobs = await workDb.ReconciliationOperations.Where(x => x.ResourceType == "sftp-account" && x.ResourceId == id && x.Status == "pending").ToListAsync(ct);
            if (jobs.Count == 0) continue;
            try
            {
                var row = await workDb.Set<SftpAccountRow>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
                var owner = row is null ? null : await workDb.Accounts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == row.OwnerId, ct);
                var username = "sftp-" + id.ToString("N");
                if (row is null || owner is null || owner.Suspended)
                    await client.DeleteAsync(id, username, ct);
                else
                {
                    if (row.Username != username) throw new InvalidOperationException("Unexpected SFTP identity.");
                    // Do not activate accounts until storage confinement is validated.
                    await client.EnsureDisabledAsync(id, username, owner.Username, row.RelativePath, ct);
                }
                foreach (var job in jobs) Complete(job);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                retry = true;
                foreach (var job in jobs) { job.Attempts++; job.LastError = "SFTP_RECONCILIATION_FAILED"; }
                logger.LogWarning("SFTP reconciliation failed for {Id}: {ErrorType}", id, ex.GetType().Name);
            }
            await workDb.SaveChangesAsync(ct);
            notifications.Publish();
        }
        return retry;
    }

    private static void Complete(ReconciliationOperation operation)
    {
        operation.Status = "completed";
        operation.CompletedAt = DateTimeOffset.UtcNow;
        operation.LastError = null;
    }
}
