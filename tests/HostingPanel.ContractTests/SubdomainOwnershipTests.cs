using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HostingPanel.Providers.CertManager;
using HostingPanel.Api;
using HostingPanel.Api.Persistence;
using HostingPanel.Providers.PowerDNS;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

internal static class SubdomainOwnershipTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync();
        await using var db = new HostingPanelDbContext(
            new DbContextOptionsBuilder<HostingPanelDbContext>().UseSqlite(connection).Options, [new SubdomainModel()]);
        await db.Database.EnsureCreatedAsync();
        HostingAccount Account(string name, Guid? creator = null) => new()
        {
            Username = name, CreatorId = creator, PasswordHash = PasswordVerifier.Hash("test-password")
        };
        var reseller = Account("reseller");
        var owner = Account("owner", reseller.Id);
        var stranger = Account("stranger");
        db.Accounts.AddRange(reseller, owner, stranger);
        var parent = new HostedDomain { Name = "example.test", OwnerId = owner.Id };
        var unrelated = new HostedDomain { Name = "unrelated.example.test", OwnerId = stranger.Id };
        var (child, link) = SubdomainOwnership.NewChild(parent, "SHOP");
        Check(child.OwnerId == owner.Id && child.Name == "shop.example.test");
        Check(link.ChildId == child.Id && link.ParentId == parent.Id && link.Label == "shop");
        db.Domains.AddRange(parent, child, unrelated);
        db.Set<SubdomainBinding>().Add(link);
        var zone = new DnsZone { Name = parent.Name, DomainId = parent.Id, OwnerId = owner.Id };
        db.DnsZones.Add(zone);
        db.Configuration.Add(new PanelConfiguration { Key = KubernetesCertificateProvider.AcmeKey(parent.Id),
            ValueJson = JsonSerializer.Serialize(DomainAcmeConfiguration.Default with { Enabled = true, Provider = "private-ca", KeyType = "ec384", PreferWildcard = true, SkipDNSNames = [parent.Name] }) });
        db.DnsRecords.Add(new DnsRecordEntity { ZoneId = zone.Id, Name = "busy.example.test.", Type = "A", Value = "192.0.2.5" });
        await db.SaveChangesAsync();
        var identity = new KubernetesIdentityService(db);
        var operations = new KubernetesOperationQueue(db);
        var notifications = new HostingOperationNotifications();
        using var http = new HttpClient { BaseAddress = new Uri("https://dns.invalid/") };
        var certificateWork = new CertificateWork();
        certificateWork.Take(); // consume startup full invalidation
        var service = new KubernetesSubdomainService(new SubdomainOwnership(db, identity), db,
            new KubernetesProvisioningService(db, new KubernetesSettingsService(db, new KubernetesBackendSettings(), identity), operations, new PowerDnsClient(http)),
            operations, notifications, certificateWork);
        AuthenticationHeaderValue Auth(string name, string password = "test-password") =>
            new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(name + ":" + password)));
        var ct = CancellationToken.None;
        Check((await service.ListAsync(parent.Name, Auth("owner"), ct)).Names!.SequenceEqual(["shop"]));
        Check((await service.ListAsync(parent.Name, Auth("reseller"), ct)).Names!.SequenceEqual(["shop"]));
        Check((await service.ListAsync(parent.Name, Auth("stranger"), ct)).Status == 403);
        Check((await service.ListAsync(parent.Name, Auth("owner", "wrong"), ct)).Status == 401);
        Check((await service.ListAsync(child.Name, Auth("owner"), ct)).Status == 400);
        Check((await service.CreateAsync(parent.Name, "new", Auth("stranger"), ct)).Status == 403);
        Check((await service.CreateAsync(parent.Name, "bad.label", Auth("owner"), ct)).Status == 400);
        Check((await service.CreateAsync(parent.Name, "unrelated", Auth("owner"), ct)).Status == 409);
        Check((await service.CreateAsync(parent.Name, "busy", Auth("owner"), ct)).Status == 409);
        Check((await service.CreateAsync(parent.Name, "new", Auth("reseller"), ct)).Status == 202);
        var created = await db.Domains.SingleAsync(x => x.Name == "new.example.test");
        Check(created.OwnerId == owner.Id);
        Check(certificateWork.Take()!.SequenceEqual([created.Id]));
        var acme = await KubernetesCertificateProvider.DomainAcmeAsync(db, created.Id, ct);
        Check(acme.Enabled && acme.Provider == "private-ca" && acme.KeyType == "ec384");
        Check(!acme.PreferWildcard && acme.SkipDNSNames.Length == 0);
        Check(KubernetesCertificateProvider.CertificateNames(created, acme).SequenceEqual([created.Name]));
        Check(!KubernetesSubdomainService.InitialCertificateIntent(DomainAcmeConfiguration.Default).Enabled);
        using var resources = new KubernetesResourceClient();
        var client = new CertManagerClient(resources, "hosting");
        using var manifest = JsonDocument.Parse(client.BuildManifest(created.Id, [created.Name], "private-ca", "Issuer", acme.KeyType));
        Check(manifest.RootElement.GetProperty("spec").GetProperty("dnsNames")[0].GetString() == created.Name);
        Check(CertManagerClient.Name(created.Id) != CertManagerClient.Name(parent.Id));
        Check(await db.Set<SubdomainBinding>().AnyAsync(x => x.ChildId == created.Id && x.ParentId == parent.Id));
        Check(await db.ReconciliationOperations.AnyAsync(x => x.ResourceId == created.Id && x.ResourceType == "website" && x.Status == "pending"));
        Check(notifications.Reader(ReconciliationTarget.Website).TryRead(out _));
        Check(notifications.Reader(ReconciliationTarget.Dns).TryRead(out _));
        Check(await db.DnsRecords.AnyAsync(x => x.ZoneId == zone.Id && x.Name == "new" && x.Type == "CNAME" && x.Value == "example.test."));
        Check(await db.ReconciliationOperations.AnyAsync(x => x.ResourceType == "dns-zone" && x.ResourceId == zone.Id));
        var php = new PhpAccountIdentity { AccountId = owner.Id, Number = 10000 };
        var vhost = WebsiteReconciler.BuildVirtualHost(created, owner.Username, php).Apache;
        Check(vhost.Contains("ServerName new.example.test"));
        Check(vhost.Contains("/home/owner/domains/new.example.test/public_html"));
        Check(vhost.Contains(AccountPhpRuntime.Name(owner.Id)));
        Check((await service.DeleteAsync(parent.Name, "shop", "other.test", Auth("owner"), ct)).Status == 400);
        Check((await service.DeleteAsync(parent.Name, "shop", child.Name, Auth("stranger"), ct)).Status == 403);
        Check((await service.DeleteAsync(parent.Name, "shop", child.Name, Auth("owner"), ct)).Status == 501);
        Check(await db.Domains.CountAsync() == 4 && await db.ReconciliationOperations.CountAsync() == 3);
        Check(await db.DnsZones.CountAsync() == 1);
    }

    private static void Check(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Subdomain ownership contract failed.");
    }
}
