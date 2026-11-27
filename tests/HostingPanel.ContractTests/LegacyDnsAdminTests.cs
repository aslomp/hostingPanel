using System.Text;
using System.Web;
using HostingPanel.Api;
using HostingPanel.Api.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

internal static class LegacyDnsAdminTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var db = new HostingPanelDbContext(
            new DbContextOptionsBuilder<HostingPanelDbContext>().UseSqlite(connection).Options, []);
        db.Database.EnsureCreated();
        var admin = new HostingAccount { Username = "admin", Kind = "admin", PasswordHash = PasswordVerifier.Hash("test-password") };
        db.Accounts.AddRange(admin, new HostingAccount { Username = "user", Kind = "user", CreatorId = admin.Id, PasswordHash = PasswordVerifier.Hash("test-password") },
            new HostingAccount { Username = "reseller", Kind = "reseller", CreatorId = admin.Id, PasswordHash = PasswordVerifier.Hash("test-password") });
        db.SaveChanges();
        var identity = new KubernetesIdentityService(db);
        var handler = new KubernetesDnsAdminCompatibility(identity, new KubernetesDnsService(db, identity, new KubernetesOperationQueue(db)));
        const string domain = "domain=example.test";
        const string create = "action=create&domain=example.test&ip=192.0.2.1&ns1=ns1.example.test&ns2=ns2.example.test";
        const string add = "action=add&domain=example.test&type=A&name=panel&value=192.0.2.1";
        Check((await Call("GET", "action=exists&" + domain)).Body == "exists=0");
        Check((await Call("POST", create)).Body.Contains("error=0"));
        Check(db.DnsZones.Count() == 1 && db.DnsRecords.Count() == 3);
        Check((await Call("GET", "action=exists&" + domain)).Body == "exists=1");
        Check((await Call("POST", add)).Body.Contains("error=0"));
        Check(db.DnsRecords.Any(r => r.Name == "panel" && r.Value == "192.0.2.1"));
        Check(db.ReconciliationOperations.Count() == 2);
        Check((await Call("POST", create)).Body.Contains("error=1"));
        Check(db.DnsZones.Count() == 1);
        Check((await Call("GET", "action=exists&" + domain, "user")).Status == 403);
        Check((await Call("POST", add, "reseller")).Status == 403);
        Check((await Call("POST", create, "admin", "wrong")).Status == 401);
        Check((await Call("GET", "action=exists&" + domain + "&domain=other.test")).Body.Contains("error=1"));
        Check((await Call("POST", create + "&reverse=yes")).Status == 501);
        Check(db.DnsRecords.Count() == 4 && db.ReconciliationOperations.Count() == 2);

        async Task<(int Status, string Body)> Call(string method, string fields, string user = "admin", string password = "test-password")
        {
            var ctx = new DefaultHttpContext();
            ctx.Request.Method = method;
            ctx.Request.Path = "/CMD_API_DNS_ADMIN";
            ctx.Request.Headers.Authorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password));
            if (method == "GET") ctx.Request.QueryString = new QueryString("?" + fields);
            else
            {
                var bytes = Encoding.UTF8.GetBytes(fields);
                ctx.Request.ContentType = "application/x-www-form-urlencoded";
                ctx.Request.ContentLength = bytes.Length;
                ctx.Request.Body = new MemoryStream(bytes);
            }
            ctx.Response.Body = new MemoryStream();
            await handler.ForwardAsync(ctx);
            return (ctx.Response.StatusCode, Encoding.UTF8.GetString(((MemoryStream)ctx.Response.Body).ToArray()));
        }
    }

    private static void Check(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Legacy DNS administration contract failed.");
    }
}
