using HostingPanel.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HostingPanel.Api;

// Time-based import for panel-owned mail records (not delegated DKIM).
// This is an independent producer of durable DNS work, not DNS polling.
internal sealed class StalwartDnsSyncScheduler(IServiceScopeFactory scopes, IConfiguration configuration,
    HostingOperationNotifications notifications, ILogger<StalwartDnsSyncScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!configuration.GetValue<bool>("Stalwart:Enabled") ||
            !configuration.GetValue("Stalwart:AutoProvisionDns", true) ||
            !configuration.GetValue("Stalwart:PeriodicDnsSync", true)) return;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var interval = TimeSpan.FromMinutes(Math.Clamp(configuration.GetValue("Stalwart:DnsSyncMinutes", 60), 5, 1440));
                try { await ScheduleDueAsync(interval, ct); }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                { logger.LogWarning(ex, "Mail DNS refresh scheduling failed"); }
                await Task.Delay(interval, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task ScheduleDueAsync(TimeSpan interval, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HostingPanelDbContext>();
        var operations = scope.ServiceProvider.GetRequiredService<KubernetesOperationQueue>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // SQLite compares DateTimeOffset poorly in SQL. Project only the latest
        // relevant operation per domain using a correlated existence query and
        // compare UTC timestamps in memory, not a full historical scan.
        var jobs = db.ReconciliationOperations.AsNoTracking().Where(x => x.ResourceType == "mail-dns");
        var candidates = await db.Domains.AsNoTracking().Where(d => !d.Suspended &&
                jobs.Any(j => j.ResourceId == d.Id) &&
                !jobs.Any(j => j.ResourceId == d.Id && j.Status == "pending"))
            .Select(d => new { d.Id, d.Name }).ToListAsync(ct);
        var cutoff = DateTimeOffset.UtcNow - interval;
        var count = 0;
        foreach (var domain in candidates)
        {
            var dates = await jobs.Where(j => j.ResourceId == domain.Id).Select(j => j.CreatedAt).ToListAsync(ct);
            if (dates.Any(date => date >= cutoff)) continue;
            operations.Schedule("mail-dns", domain.Id, "update", new { domain.Name });
            count++;
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        if (count != 0) notifications.PublishChange(ReconciliationTarget.Dns);
    }
}
