using System.Net.Http.Headers;
using System.Text.Json;
using HostingPanel.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HostingPanel.Api;

// Subdomain website intent uses the existing shared website worker.
public sealed class KubernetesSubdomainService(SubdomainOwnership ownership, HostingPanelDbContext db,
    KubernetesProvisioningService provisioning, KubernetesOperationQueue operations, HostingOperationNotifications notifications, CertificateWork certificateWork) : ISubdomains
{
    public async Task<SubdomainInventory> ListAsync(string domain, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var (status, parent, error) = await ownership.ResolveAsync(domain, auth, ct);
        return parent is null ? new(status, null, error)
            : new(200, await ownership.ListLabelsAsync(parent, ct), null);
    }

    public async Task<SubdomainMutation> CreateAsync(string domain, string label, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (status, parent, error) = await ownership.ResolveAsync(domain, auth, ct);
        if (parent is null) return new(status, error);
        if (!SubdomainNames.ValidLabel(label) || !SubdomainNames.ValidDomain(label + "." + parent.Name))
            return new(400, "INVALID_SUBDOMAIN");
        if (parent.Suspended) return new(409, "DOMAIN_SUSPENDED");
        var (child, binding) = SubdomainOwnership.NewChild(parent, label);
        if (await db.Domains.AnyAsync(x => x.Name == child.Name, ct) ||
            await db.DnsZones.AnyAsync(x => x.Name == child.Name, ct))
            return new(409, "SUBDOMAIN_EXISTS");
        if (await db.Configuration.AnyAsync(x => x.Key == "DeletingDomain:" + child.Name, ct))
            return new(409, "DOMAIN_DELETION_PENDING");
        var dnsError = await provisioning.StageSubdomainDnsAsync(parent, child, binding, ct);
        if (dnsError is "SUBDOMAIN_DNS_CONFLICT" or "SUBDOMAIN_DNS_OWNERSHIP_CONFLICT")
            return new(409, dnsError);
        if (dnsError is not null)
            db.ReconciliationOperations.Add(new ReconciliationOperation {
                ResourceType = "domain-dns", ResourceId = child.Id, Action = "create", LastError = dnsError
            });
        db.Domains.Add(child);
        db.Set<SubdomainBinding>().Add(binding);
        // Copy certificate intent, not the parent's certificate, wildcard or secret.
        // The child has its own lifecycle and can subsequently change its own settings.
        var parentAcme = await KubernetesCertificateProvider.DomainAcmeAsync(db, parent.Id, ct);
        var childAcme = InitialCertificateIntent(parentAcme);
        db.Configuration.Add(new PanelConfiguration {
            Key = KubernetesCertificateProvider.AcmeKey(child.Id),
            ValueJson = JsonSerializer.Serialize(childAcme)
        });
        operations.Schedule("domain", child.Id, "create", new { child.Name });
        await provisioning.ScheduleWebsiteAsync(child, "create", ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        certificateWork.DomainChanged(child.Id);
        notifications.PublishChange(ReconciliationTarget.Website | ReconciliationTarget.Dns);
        return new(202, null);
    }

    internal static DomainAcmeConfiguration InitialCertificateIntent(DomainAcmeConfiguration parent) =>
        DomainAcmeConfiguration.Default with {
            Enabled = parent.Enabled,
            Provider = parent.Provider,
            KeyType = parent.KeyType
        };

    public async Task<SubdomainMutation> DeleteAsync(string domain, string label, string confirmation, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (status, parent, error) = await ownership.ResolveAsync(domain, auth, ct);
        if (parent is null) return new(status, error);
        if (!SubdomainNames.ValidLabel(label) || !SubdomainNames.ValidDomain(label + "." + parent.Name))
            return new(400, "INVALID_SUBDOMAIN");
        label = label.ToLowerInvariant();
        var name = label + "." + parent.Name;
        if (confirmation != name) return new(400, "CONFIRMATION_REQUIRED");
        var binding = await db.Set<SubdomainBinding>().SingleOrDefaultAsync(x => x.ParentId == parent.Id && x.Label == label, ct);
        if (binding is null) return new(404, "SUBDOMAIN_NOT_FOUND");
        var child = await db.Domains.SingleAsync(x => x.Id == binding.ChildId, ct);
        if (child.OwnerId != parent.OwnerId || child.Name != name) return new(409, "SUBDOMAIN_OWNERSHIP_CONFLICT");
        if (await db.Configuration.AnyAsync(x => x.Key == "DeletingDomain:" + name, ct))
            return new(409, "DOMAIN_DELETION_PENDING");
        var cleanupError = await StageDeletionAsync(parent, child, binding, ct);
        if (cleanupError is not null) return new(409, cleanupError);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        certificateWork.DomainChanged(child.Id);
        notifications.PublishChange(ReconciliationTarget.Website | ReconciliationTarget.Dns);
        return new(202, null);
    }

    private async Task<string?> StageDeletionAsync(HostedDomain parent, HostedDomain child, SubdomainBinding binding, CancellationToken ct)
    {
        var zoneIds = new List<Guid>();
        var zone = await db.DnsZones.SingleOrDefaultAsync(x => x.Name == parent.Name, ct);
        if (zone is not null && (zone.OwnerId != parent.OwnerId || zone.DomainId != parent.Id))
            return "SUBDOMAIN_DNS_OWNERSHIP_CONFLICT";
        if (await db.DnsZones.AnyAsync(x => x.DomainId == child.Id || x.Name == child.Name, ct))
            return "SUBDOMAIN_DNS_CONFLICT";
        if (binding.GeneratedDnsRecordId is { } recordId)
        {
            var record = await db.DnsRecords.FindAsync([recordId], ct);
            if (record is not null)
            {
                if (zone is null || record.ZoneId != zone.Id) return "SUBDOMAIN_DNS_OWNERSHIP_CONFLICT";
                // DNS editing replaces the row ID; never delete a manually replaced record.
                if (record.Type == "CNAME" && record.Name == binding.Label && record.Value == parent.Name + ".")
                {
                    db.DnsRecords.Remove(record);
                    operations.Schedule("dns-zone", zone.Id, "update", new { zone.Name });
                    zoneIds.Add(zone.Id);
                }
            }
        }
        var owner = await db.Accounts.SingleAsync(x => x.Id == parent.OwnerId, ct);
        var access = await db.FileAccessAccounts.Where(x => x.DomainId == child.Id).ToListAsync(ct);
        db.FileAccessAccounts.RemoveRange(access);
        var acme = await db.Configuration.FindAsync([KubernetesCertificateProvider.AcmeKey(child.Id)], ct);
        if (acme is not null) db.Configuration.Remove(acme);
        var deferred = await db.ReconciliationOperations.Where(x => x.ResourceType == "domain-dns" &&
            x.ResourceId == child.Id && x.Status == "pending").ToListAsync(ct);
        foreach (var job in deferred)
        {
            job.Status = "completed";
            job.CompletedAt = DateTimeOffset.UtcNow;
            job.LastError = null;
        }
        db.Configuration.Add(new PanelConfiguration { Key = "DeletingDomain:" + child.Name,
            ValueJson = JsonSerializer.Serialize(new { id = child.Id, username = owner.Username }) });
        operations.Schedule("domain-cleanup", child.Id, "delete", new { name = child.Name, username = owner.Username, zoneIds });
        operations.Schedule("domain", child.Id, "delete", new { child.Name });
        await provisioning.ScheduleWebsiteAsync(child, "delete", ct);
        db.Set<SubdomainBinding>().Remove(binding);
        db.Domains.Remove(child);
        return null;
    }
}
