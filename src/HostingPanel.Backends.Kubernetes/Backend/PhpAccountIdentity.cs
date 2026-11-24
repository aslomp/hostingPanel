using HostingPanel.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HostingPanel.Api;

// Provider-owned allocation; never reuse a UID or port while retained NFS data may exist.
public sealed class PhpAccountIdentity
{
    public Guid AccountId { get; set; }
    public int Number { get; set; }
    public int Uid => Number;
    public int Gid => Number;
    public int Port => Number;
    public string UnixName => "hp" + Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public sealed class PhpIdentityModel : IHostingPanelModelExtension, IHostingPanelSchemaExtension
{
    public string Provider => "Kubernetes.Php";
    public IReadOnlyList<HostingPanelSchemaMigration> Migrations => [
        new(1, """CREATE TABLE IF NOT EXISTS "PhpAccountIdentities" ("AccountId" TEXT NOT NULL PRIMARY KEY, "Number" INTEGER NOT NULL UNIQUE CHECK ("Number" BETWEEN 10000 AND 30000));""")
    ];

    public void Configure(ModelBuilder model)
    {
        model.Entity<PhpAccountIdentity>(e => {
            e.ToTable("PhpAccountIdentities");
            e.HasKey(x => x.AccountId);
            e.HasIndex(x => x.Number).IsUnique();
            e.Ignore(x => x.Uid);
            e.Ignore(x => x.Gid);
            e.Ignore(x => x.Port);
            e.Ignore(x => x.UnixName);
        });
    }

    public static async Task<Dictionary<Guid, PhpAccountIdentity>> AllocateAsync(
        HostingPanelDbContext db, IEnumerable<Guid> owners, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var identities = await db.Set<PhpAccountIdentity>().ToDictionaryAsync(x => x.AccountId, ct);
        var next = identities.Count == 0 ? 10000 : identities.Values.Max(x => x.Number) + 1;
        foreach (var owner in owners.Distinct().Order())
        {
            if (identities.ContainsKey(owner)) continue;
            if (next > 30000) throw new InvalidOperationException("PHP identity range exhausted");
            var identity = new PhpAccountIdentity { AccountId = owner, Number = next++ };
            db.Set<PhpAccountIdentity>().Add(identity);
            identities.Add(owner, identity);
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return identities;
    }
}
