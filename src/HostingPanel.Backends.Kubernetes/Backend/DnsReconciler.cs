using Microsoft.Extensions.Configuration;
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
        await ReconciliationLoop.RunAsync(notifications.Reader(ReconciliationTarget.Dns),
            ReconcileAsync, TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan, logger, ct);
    }

    private async Task<bool> ReconcileAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HostingPanelDbContext>();
        var backend = scope.ServiceProvider.GetRequiredService<KubernetesDnsService>();
        var provider = new PowerDnsProvider(backend, scope.ServiceProvider.GetRequiredService<PowerDnsClient>());
        // Stage deferred zone creation from durable domain requests. A new scope
        // per request prevents failed work from leaking tracked changes into other domains.
        var retry = false;
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
                if (DnsRetryPolicy.RequiresRetry(error)) retry = true;
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
            { retry = true; logger.LogWarning(ex, "Deferred DNS provisioning failed for operation {Id}", id); }
        }
        var config = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
        var mailJobs = await db.ReconciliationOperations.AsNoTracking()
            .Where(x => x.ResourceType == "mail-dns" && x.Status == "pending")
            .Select(x => x.Id).ToListAsync(ct);
        foreach (var id in mailJobs)
        {
            using var mailScope = scopes.CreateScope();
            var mailDb = mailScope.ServiceProvider.GetRequiredService<HostingPanelDbContext>();
            var job = await mailDb.ReconciliationOperations.SingleOrDefaultAsync(x => x.Id == id && x.Status == "pending", ct);
            if (job is null) continue;
            try
            {
                var stage = mailScope.ServiceProvider.GetRequiredService<StalwartDnsProvisioning>();
                var previousError = job.LastError;
                job.LastError = await stage.StageAsync(job.ResourceId, ct);
                if (job.LastError is null)
                {
                    job.Status = "completed";
                    job.CompletedAt = DateTimeOffset.UtcNow;
                }
                else
                {
                    if (DnsRetryPolicy.RequiresRetry(job.LastError)) retry = true;
                    if (DnsRetryPolicy.RequiresRetry(job.LastError) || previousError != job.LastError) job.Attempts++;
                }
                await mailDb.SaveChangesAsync(ct);
                if (job.LastError is null || previousError != job.LastError) notifications.Publish();
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                retry = true;
                mailDb.ChangeTracker.Clear();
                var failed = await mailDb.ReconciliationOperations.SingleOrDefaultAsync(x => x.Id == id && x.Status == "pending", ct);
                if (failed is not null)
                {
                    failed.Attempts++;
                    failed.LastError = "MAIL_DNS_RECONCILIATION_FAILED";
                    await mailDb.SaveChangesAsync(ct);
                }
                logger.LogWarning("Mail DNS reconciliation failed for operation {Id}: {ErrorType}", id, ex.GetType().Name);
            }
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
                    var delegated = zone.DomainId.HasValue && StalwartDnsProviderRegistration.Enabled(config)
                        && await db.ReconciliationOperations.AnyAsync(x => x.ResourceType == "mail-dns" && x.ResourceId == zone.DomainId.Value, ct);
                    await provider.ProvisionAsync(name, rows.Select(x => new DnsRecord(x.Type, x.Name, x.Value, x.Ttl.ToString())).ToArray(), ct, delegated);
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
                retry = true;
                foreach (var row in group) { row.Attempts++; row.LastError = "DNS reconciliation failed; see application log."; }
                logger.LogWarning(ex, "Could not reconcile DNS zone {Id}", group.Key);
            }
            await db.SaveChangesAsync(ct);
            notifications.Publish();
            if (group.All(row => row.Status == "completed"))
            {
                notifications.Signal(ReconciliationTarget.Website);
                if (await db.ReconciliationOperations.AnyAsync(x => x.ResourceType == "domain-dns" &&
                    x.Status == "pending" && x.LastError == "SUBDOMAIN_PARENT_ZONE_NOT_READY", ct))
                    notifications.Signal(ReconciliationTarget.Dns);
            }
        }
        // Execute explicit shared-record commands in chronological order, never
        // coalesce them into a desired-state replacement of the whole zone.
        var edits = await db.ReconciliationOperations.Where(x => x.ResourceType == "dns-record" && x.Status == "pending").ToListAsync(ct);
        var blockedZones = new HashSet<Guid>();
        foreach (var edit in edits.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id))
        {
            if (blockedZones.Contains(edit.ResourceId)) continue;
            try
            {
                var zone = await db.DnsZones.AsNoTracking().SingleOrDefaultAsync(x => x.Id == edit.ResourceId, ct);
                if (zone is not null)
                {
                    if (await db.ReconciliationOperations.AnyAsync(x => x.ResourceType == "dns-zone" &&
                        x.ResourceId == zone.Id && x.Status == "pending", ct))
                    {
                        retry = true;
                        blockedZones.Add(edit.ResourceId);
                        continue;
                    }
                    using var payload = JsonDocument.Parse(edit.PayloadJson);
                    var change = payload.RootElement.GetProperty("Change").Deserialize<DnsChange>()
                        ?? throw new InvalidOperationException("Missing DNS change.");
                    await scope.ServiceProvider.GetRequiredService<PowerDnsClient>().ChangeSharedRecordAsync(zone.Name, change, ct);
                }
                edit.Status = "completed";
                edit.CompletedAt = DateTimeOffset.UtcNow;
                edit.LastError = null;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                retry = true;
                blockedZones.Add(edit.ResourceId);
                edit.Attempts++;
                edit.LastError = "DNS record publication failed; see application log.";
                logger.LogWarning(ex, "DNS record publication failed for operation {Id}", edit.Id);
            }
            await db.SaveChangesAsync(ct);
            notifications.Publish();
        }
        return retry;
    }
}
