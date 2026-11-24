using System.Text.Json;
using HostingPanel.Api.Adapters;
using HostingPanel.Api.Persistence;
using HostingPanel.Providers.PowerDNS;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HostingPanel.Api;

public sealed class DnsReconciler(IServiceScopeFactory scopes, ILogger<DnsReconciler> logger, HostingOperationNotifications notifications) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        do
        {
            try { await ReconcileAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning(ex, "DNS reconciliation failed"); }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HostingPanelDbContext>();
        var backend = scope.ServiceProvider.GetRequiredService<IDNSProvider>();
        var provider = new PowerDnsProvider(backend, scope.ServiceProvider.GetRequiredService<PowerDnsClient>());
        // Stage deferred zone creation from durable domain requests. A new scope
        // per request prevents failed work from leaking tracked changes into other domains.
        var deferred = await db.ReconciliationOperations.AsNoTracking().Where(x => x.ResourceType == "domain-dns" && x.Status == "pending").Select(x => x.Id).ToListAsync(ct);
        foreach (var id in deferred)
        {
            try
            {
                using var retryScope = scopes.CreateScope();
                var retryDb = retryScope.ServiceProvider.GetRequiredService<HostingPanelDbContext>();
                var retryBackend = retryScope.ServiceProvider.GetRequiredService<IDomainDnsStaging>();
                await using var tx = await retryDb.Database.BeginTransactionAsync(ct);
                var operation = await retryDb.ReconciliationOperations.SingleOrDefaultAsync(x => x.Id == id && x.Status == "pending", ct);
                if (operation is null) continue;
                var domain = await retryDb.Domains.SingleOrDefaultAsync(x => x.Id == operation.ResourceId, ct);
                var error = domain is null ? null : await retryBackend.StageDomainDnsAsync(domain, ct, retry: true);
                var changed = operation.LastError != error;
                if (error is null)
                {
                    operation.Status = "completed";
                    operation.CompletedAt = DateTimeOffset.UtcNow;
                }
                else if (changed) operation.Attempts++;
                operation.LastError = error;
                if (changed || error is null)
                {
                    await retryDb.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);
                    notifications.Publish();
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            { logger.LogWarning(ex, "Deferred DNS provisioning failed for operation {Id}", id); }
        }
        var pending = await db.ReconciliationOperations.Where(x => x.ResourceType == "dns-zone" && x.Status == "pending").ToListAsync(ct);
        foreach (var group in pending.GroupBy(x => x.ResourceId))
        {
            try
            {
                var latest = group.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).First();
                using var payload = JsonDocument.Parse(latest.PayloadJson);
                var name = payload.RootElement.GetProperty("Name").GetString()!;
                // Resolve by name too: a deleted zone may have been recreated under a new ID.
                var zone = await db.DnsZones.AsNoTracking().SingleOrDefaultAsync(x => x.Name == name, ct);
                if (zone is null)
                    await provider.DeprovisionAsync(name, ct);
                else
                {
                    var rows = await db.DnsRecords.AsNoTracking().Where(x => x.ZoneId == zone.Id).ToListAsync(ct);
                    await provider.ProvisionAsync(name, rows.Select(x => new DnsRecord(x.Type, x.Name, x.Value, x.Ttl.ToString())).ToArray(), ct);
                }
                foreach (var row in group)
                {
                    row.Status = "completed";
                    row.CompletedAt = DateTimeOffset.UtcNow;
                    row.LastError = null;
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                foreach (var row in group) { row.Attempts++; row.LastError = "DNS reconciliation failed; see application log."; }
                logger.LogWarning(ex, "Could not reconcile DNS zone {Id}", group.Key);
            }
            await db.SaveChangesAsync(ct);
            notifications.Publish();
        }
    }
}
