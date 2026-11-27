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
            !configuration.GetValue("Stalwart:PeriodicDnsSync", false) ||
            StalwartDnsProviderRegistration.Enabled(configuration)) return;
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
        // Use SQLite's date conversion for DateTimeOffset instead of loading
        // historical operations or issuing one history query per domain.
        var cutoff = DateTimeOffset.UtcNow - interval;
        var dueIds = await db.Database.SqlQuery<Guid>($"""
            SELECT d.Id AS Value FROM Domains d
            WHERE d.Suspended = 0
              AND EXISTS (SELECT 1 FROM ReconciliationOperations j
                  WHERE j.ResourceType = 'mail-dns' AND j.ResourceId = d.Id)
              AND NOT EXISTS (SELECT 1 FROM ReconciliationOperations j
                  WHERE j.ResourceType = 'mail-dns' AND j.ResourceId = d.Id
                    AND (j.Status = 'pending' OR julianday(j.CreatedAt) >= julianday({cutoff.ToString("O")})))
            """).ToListAsync(ct);
        var candidates = await db.Domains.AsNoTracking().Where(d => dueIds.Contains(d.Id))
            .Select(d => new { d.Id, d.Name }).ToListAsync(ct);
        var count = 0;
        foreach (var domain in candidates)
        {
            operations.Schedule("mail-dns", domain.Id, "update", new { domain.Name });
            count++;
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        if (count != 0) notifications.PublishChange(ReconciliationTarget.Dns);
    }
}
