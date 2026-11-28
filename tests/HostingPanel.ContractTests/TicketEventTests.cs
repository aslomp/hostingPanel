using System.Net.Http.Headers;
using System.Text;
using HostingPanel.Api;
using HostingPanel.Api.Persistence;
using HostingPanel.Api.Services;
using HostingPanel.Web.Realtime;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

internal static class TicketEventTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<HostingPanelDbContext>().UseSqlite(connection).Options;
        await using var db = new HostingPanelDbContext(options, []);
        await db.Database.EnsureCreatedAsync();
        var alice = new HostingAccount { Username = "alice", PasswordHash = PasswordVerifier.Hash("test-password") };
        var bob = new HostingAccount { Username = "bob", PasswordHash = PasswordVerifier.Hash("test-password") };
        db.Accounts.AddRange(alice, bob);
        await db.SaveChangesAsync();
        var events = new TicketEvents();
        var service = new KubernetesTicketService(db, new KubernetesIdentityService(db), events);
        AuthenticationHeaderValue Auth(string name) =>
            new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(name + ":test-password")));
        var ct = CancellationToken.None;
        Check((await service.CreateAsync(new("", ""), Auth("alice"), ct)).Status == 400);
        Check(!events.Reader.TryRead(out _));
        Check((await service.CreateAsync(new("Help", "Body"), Auth("alice"), ct)).Status == 200);
        Check(events.Reader.TryRead(out var created) && created.Account == "alice" && created.Channel is null);
        await using var observed = new HostingPanelDbContext(options, []);
        var ticket = await observed.Tickets.AsNoTracking().SingleAsync();
        Check(ticket.OwnerId == alice.Id && ticket.Subject == "Help");
        var id = ticket.Id.ToString();
        Check((await service.ReplyAsync(id, new("Denied", "creator", "Help", "request"), Auth("bob"), ct)).Status == 403);
        Check(!events.Reader.TryRead(out _));
        Check((await service.ReplyAsync(id, new("Reply", "creator", "Help", "request"), Auth("alice"), ct)).Status == 200);
        Check(events.Reader.TryRead(out var reply) && reply.Account == "alice");
        Check(await observed.TicketReplies.AnyAsync(x => x.TicketId == ticket.Id && x.Text == "Reply"));
        Check((await service.SetStatusAsync(id, new("request", false), Auth("alice"), ct)).Status == 200);
        Check(events.Reader.TryRead(out var status) && status.Account == "alice");
        Check(!await observed.Tickets.Where(x => x.Id == ticket.Id).Select(x => x.Open).SingleAsync());
        Check(!events.Reader.TryRead(out _));
        var registry = new TicketChannelRegistry();
        var connected = true;
        var first = registry.Register("alice", () => connected);
        var second = registry.Register("alice");
        var other = registry.Register("bob");
        Check(registry.ForAccount("alice").ToHashSet().SetEquals([first, second]));
        connected = false;
        Check(registry.ForAccount("alice").SequenceEqual([second]));
        registry.Revoke(second);
        Check(registry.ForAccount("alice").Length == 0 && registry.IsActive(other));
    }

    private static void Check(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Ticket event contract failed.");
    }
}
