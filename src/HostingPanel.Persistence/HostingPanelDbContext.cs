using Microsoft.EntityFrameworkCore;

namespace HostingPanel.Api.Persistence;

/// <summary>Shared HostingPanel control-plane state, independent of the hosting backend.</summary>
public sealed class HostingPanelDbContext(DbContextOptions<HostingPanelDbContext> options, IEnumerable<IHostingPanelModelExtension> extensions) : DbContext(options)
{
    public DbSet<HostingAccount> Accounts => Set<HostingAccount>();
    public DbSet<HostingPackage> Packages => Set<HostingPackage>();
    public DbSet<HostedDomain> Domains => Set<HostedDomain>();
    public DbSet<DnsZone> DnsZones => Set<DnsZone>();
    public DbSet<DnsRecordEntity> DnsRecords => Set<DnsRecordEntity>();
    public DbSet<HostedDatabase> Databases => Set<HostedDatabase>();
    public DbSet<DatabaseUser> DatabaseUsers => Set<DatabaseUser>();
    public DbSet<DatabaseGrant> DatabaseGrants => Set<DatabaseGrant>();
    public DbSet<FileAccessAccount> FileAccessAccounts => Set<FileAccessAccount>();
    public DbSet<TlsCertificate> Certificates => Set<TlsCertificate>();
    public DbSet<KubernetesResourceBinding> ResourceBindings => Set<KubernetesResourceBinding>();
    public DbSet<PanelConfiguration> Configuration => Set<PanelConfiguration>();
    public DbSet<HostedTicket> Tickets => Set<HostedTicket>();
    public DbSet<HostedTicketReply> TicketReplies => Set<HostedTicketReply>();
    public DbSet<HostedMessage> Messages => Set<HostedMessage>();
    public DbSet<ReconciliationOperation> ReconciliationOperations => Set<ReconciliationOperation>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<HostingAccount>(e => {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Username).IsUnique();
            e.HasOne<HostingAccount>().WithMany().HasForeignKey(x => x.CreatorId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<HostingPackage>().WithMany().HasForeignKey(x => x.PackageId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<HostingPackage>(e => {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.OwnerId, x.Name }).IsUnique();
            e.HasOne<HostingAccount>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<HostedDomain>(e => {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Name).IsUnique();
            e.HasOne<HostingAccount>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<DnsZone>(e => {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Name).IsUnique();
            e.HasOne<HostedDomain>().WithMany().HasForeignKey(x => x.DomainId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<HostingAccount>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<DnsRecordEntity>(e => {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.ZoneId, x.Name, x.Type });
            e.HasOne<DnsZone>().WithMany().HasForeignKey(x => x.ZoneId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<HostedDatabase>(e => {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Name).IsUnique();
            e.HasOne<HostingAccount>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<DatabaseUser>(e => {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.OwnerId, x.Name }).IsUnique();
            e.HasOne<HostingAccount>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<DatabaseGrant>(e => {
            e.HasKey(x => new { x.DatabaseId, x.DatabaseUserId });
            e.HasOne<HostedDatabase>().WithMany().HasForeignKey(x => x.DatabaseId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<DatabaseUser>().WithMany().HasForeignKey(x => x.DatabaseUserId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<FileAccessAccount>(e => {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.OwnerId, x.Username }).IsUnique();
            e.HasOne<HostingAccount>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<HostedDomain>().WithMany().HasForeignKey(x => x.DomainId).OnDelete(DeleteBehavior.SetNull);
        });
        model.Entity<TlsCertificate>(e => {
            e.HasKey(x => x.Id);
            e.HasOne<HostedDomain>().WithMany().HasForeignKey(x => x.DomainId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<KubernetesResourceBinding>(e => {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.Cluster, x.Namespace, x.Kind, x.Name }).IsUnique();
            e.HasIndex(x => new { x.ResourceType, x.ResourceId });
        });
        model.Entity<PanelConfiguration>().HasKey(x => x.Key);
        model.Entity<ReconciliationOperation>(e => {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.Status, x.CreatedAt });
            e.HasIndex(x => new { x.ResourceType, x.ResourceId });
        });
        model.Entity<HostedTicket>(e => {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.OwnerId);
            e.HasOne<HostingAccount>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<HostedTicketReply>(e => {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TicketId);
            e.HasOne<HostedTicket>().WithMany().HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<HostedMessage>(e => {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.RecipientId);
            e.HasOne<HostingAccount>().WithMany().HasForeignKey(x => x.RecipientId).OnDelete(DeleteBehavior.Restrict);
        });
        foreach (var extension in extensions)
            extension.Configure(model);
    }
}

public sealed class HostingAccount {
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Username { get; set; } = "";
    public string Kind { get; set; } = "user";
    public string Email { get; set; } = "";
    public string? PasswordHash { get; set; }
    public Guid? CreatorId { get; set; }
    public Guid? PackageId { get; set; }
    public bool Suspended { get; set; }
    public string? LimitsJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class HostingPackage {
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? OwnerId { get; set; }
    public string Name { get; set; } = "";
    public string LimitsJson { get; set; } = "{}";
}
public sealed class HostedDomain {
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerId { get; set; }
    public string Name { get; set; } = "";
    public string SettingsJson { get; set; } = "{}";
    public bool Suspended { get; set; }
}
public sealed class DnsZone {
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerId { get; set; }
    public Guid? DomainId { get; set; }
    public string Name { get; set; } = "";
    public int DefaultTtl { get; set; } = 3600;
}
public sealed class DnsRecordEntity {
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ZoneId { get; set; }
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string Value { get; set; } = "";
    public int Ttl { get; set; } = 3600;
    public int Priority { get; set; }
}
public sealed class HostedDatabase {
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerId { get; set; }
    public string Name { get; set; } = "";
    public string Engine { get; set; } = "mysql";
    public string? KubernetesSecretName { get; set; }
}
public sealed class DatabaseUser {
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerId { get; set; }
    public string Name { get; set; } = "";
    public string? CredentialSecretReference { get; set; }
}
public sealed class DatabaseGrant {
    public Guid DatabaseId { get; set; }
    public Guid DatabaseUserId { get; set; }
    public string PrivilegesJson { get; set; } = "[]";
}
public sealed class FileAccessAccount {
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerId { get; set; }
    public Guid? DomainId { get; set; }
    public string Username { get; set; } = "";
    public string Protocol { get; set; } = "sftp";
    public string RootPath { get; set; } = "/";
    public string? CredentialSecretReference { get; set; }
}
public sealed class TlsCertificate {
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DomainId { get; set; }
    public string Issuer { get; set; } = "";
    public string? KubernetesSecretName { get; set; }
    public string DesiredState { get; set; } = "requested";
}
public sealed class KubernetesResourceBinding {
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ResourceType { get; set; } = "";
    public Guid ResourceId { get; set; }
    public string Cluster { get; set; } = "default";
    public string Namespace { get; set; } = "default";
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public string? KubernetesUid { get; set; }
    public long? ObservedGeneration { get; set; }
}
public sealed class PanelConfiguration {
    public string Key { get; set; } = "";
    public string ValueJson { get; set; } = "{}";
}

public sealed class HostedTicket {
    public long Id { get; set; }
    public Guid OwnerId { get; set; }
    public string Subject { get; set; } = "";
    public string Text { get; set; } = "";
    public string Recipient { get; set; } = "creator";
    public int Priority { get; set; } = 20;
    public bool Open { get; set; } = true;
    public bool Unread { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class HostedTicketReply {
    public long Id { get; set; }
    public long TicketId { get; set; }
    public Guid AuthorId { get; set; }
    public string Text { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class HostedMessage {
    public long Id { get; set; }
    public Guid RecipientId { get; set; }
    public string From { get; set; } = "";
    public string Subject { get; set; } = "";
    public bool Unread { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ReconciliationOperation {
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ResourceType { get; set; } = "";
    public Guid ResourceId { get; set; }
    public string Action { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
    public string Status { get; set; } = "pending";
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
}
