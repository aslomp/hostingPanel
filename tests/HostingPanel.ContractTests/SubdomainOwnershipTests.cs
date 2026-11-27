using System.Net.Http.Headers;
using System.Text;
using HostingPanel.Api;
using HostingPanel.Api.Persistence;
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
        await db.SaveChangesAsync();
        var service = new KubernetesSubdomainService(new SubdomainOwnership(db, new KubernetesIdentityService(db)));
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
        Check((await service.CreateAsync(parent.Name, "new", Auth("owner"), ct)).Status == 501);
        Check((await service.DeleteAsync(parent.Name, "shop", "other.test", Auth("owner"), ct)).Status == 400);
        Check((await service.DeleteAsync(parent.Name, "shop", child.Name, Auth("stranger"), ct)).Status == 403);
        Check((await service.DeleteAsync(parent.Name, "shop", child.Name, Auth("owner"), ct)).Status == 501);
        Check(await db.Domains.CountAsync() == 3 && !await db.ReconciliationOperations.AnyAsync());
    }

    private static void Check(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Subdomain ownership contract failed.");
    }
}
