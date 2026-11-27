using System.Net.Http.Headers;
using HostingPanel.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HostingPanel.Api;

// The child remains a HostedDomain for website provisioning. The relation
// is explicit, never inferred from a suffix or a caller-supplied owner ID.
public sealed class SubdomainBinding
{
    public Guid ChildId { get; set; }
    public Guid ParentId { get; set; }
    public string Label { get; set; } = "";
}

public sealed class SubdomainModel : IHostingPanelModelExtension, IHostingPanelSchemaExtension
{
    public string Provider => "Kubernetes.Subdomains";
    public IReadOnlyList<HostingPanelSchemaMigration> Migrations => [
        new(1, """
            CREATE TABLE IF NOT EXISTS "SubdomainBindings" (
                "ChildId" TEXT NOT NULL PRIMARY KEY REFERENCES "Domains" ("Id") ON DELETE CASCADE,
                "ParentId" TEXT NOT NULL REFERENCES "Domains" ("Id") ON DELETE RESTRICT,
                "Label" TEXT NOT NULL,
                CHECK ("ChildId" <> "ParentId"),
                UNIQUE ("ParentId", "Label")
            );
            """)
    ];

    public void Configure(ModelBuilder model)
    {
        model.Entity<SubdomainBinding>(e =>
        {
            e.ToTable("SubdomainBindings");
            e.HasKey(x => x.ChildId);
            e.HasIndex(x => new { x.ParentId, x.Label }).IsUnique();
            e.HasOne<HostedDomain>().WithMany().HasForeignKey(x => x.ChildId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<HostedDomain>().WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}

public sealed class SubdomainOwnership(HostingPanelDbContext db, KubernetesIdentityService identity)
{
    internal async Task<(int Status, HostedDomain? Parent, string? Error)> ResolveAsync(
        string domain, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var principal = await identity.AuthenticateAsync(auth, ct);
        if (principal is null) return (401, null, "UNAUTHORIZED");
        if (!SubdomainNames.ValidDomain(domain)) return (400, null, "INVALID_DOMAIN");
        var visible = await identity.VisibleAccountsAsync(principal, ct);
        var name = domain.ToLowerInvariant();
        var parent = await db.Domains.SingleOrDefaultAsync(x => x.Name == name, ct);
        if (parent is null) return (404, null, "DOMAIN_NOT_FOUND");
        if (!visible.Contains(parent.OwnerId)) return (403, null, "ACCESS_DENIED");
        if (await db.Set<SubdomainBinding>().AnyAsync(x => x.ChildId == parent.Id, ct))
            return (400, null, "SUBDOMAIN_PARENT_REQUIRED");
        return (200, parent, null);
    }

    // Only the trusted backend constructs children. Persist child and binding
    // in the same transaction as DNS and website intent in the next stage.
    internal static (HostedDomain Child, SubdomainBinding Binding) NewChild(HostedDomain parent, string label)
    {
        if (!SubdomainNames.ValidLabel(label) || !SubdomainNames.ValidDomain(label + "." + parent.Name))
            throw new ArgumentException("Invalid subdomain label.", nameof(label));
        label = label.ToLowerInvariant();
        var child = new HostedDomain
        {
            OwnerId = parent.OwnerId,
            Name = label + "." + parent.Name,
            SettingsJson = parent.SettingsJson,
            Suspended = parent.Suspended
        };
        return (child, new SubdomainBinding { ChildId = child.Id, ParentId = parent.Id, Label = label });
    }

    internal async Task<IReadOnlyList<string>> ListLabelsAsync(HostedDomain parent, CancellationToken ct) =>
        await (from link in db.Set<SubdomainBinding>().AsNoTracking()
               join child in db.Domains.AsNoTracking() on link.ChildId equals child.Id
               where link.ParentId == parent.Id && child.OwnerId == parent.OwnerId
               orderby link.Label
               select link.Label).ToListAsync(ct);
}
