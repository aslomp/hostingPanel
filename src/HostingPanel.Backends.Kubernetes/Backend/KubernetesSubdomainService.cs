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
        var dnsError = await provisioning.StageSubdomainDnsAsync(parent, child, binding.Label, ct);
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

    public Task<SubdomainMutation> DeleteAsync(string domain, string label, string confirmation, AuthenticationHeaderValue auth, CancellationToken ct)
        => ValidateAsync(domain, label, confirmation, true, auth, ct);

    private async Task<SubdomainMutation> ValidateAsync(string domain, string label, string? confirmation,
        bool deleting, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var (status, parent, error) = await ownership.ResolveAsync(domain, auth, ct);
        if (parent is null) return new(status, error);
        if (!SubdomainNames.ValidLabel(label) || !SubdomainNames.ValidDomain(label + "." + parent.Name))
            return new(400, "INVALID_SUBDOMAIN");
        if (deleting && confirmation != label.ToLowerInvariant() + "." + parent.Name)
            return new(400, "CONFIRMATION_REQUIRED");
        return new(501, "SUBDOMAIN_PROVISIONING_NOT_IMPLEMENTED");
    }
}
