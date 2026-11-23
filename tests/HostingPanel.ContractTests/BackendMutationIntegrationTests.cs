using System.Net;
using System.Net.Http.Headers;
using System.Text;
using HostingPanel.Api.Services;

public static class BackendMutationIntegrationTests
{
    public static void Run()
    {
        var backend = new FakeBackend();
        var factory = new FakeFactory(backend);
        var auth = Basic("admin", "valid");
        var accounts = new AccountMutationService(factory);
        var dns = new DnsZoneService(factory);

        var createdUser = accounts.CreateAsync(
            new("user", "alice", "alice@example.com", "secret123",
                "alice.example.com", "starter"), auth, CancellationToken.None)
            .GetAwaiter().GetResult();
        Require(createdUser.Status == 200 && createdUser.Error is null, "Create user");
        Require(backend.Writes.Last().Path == "CMD_API_ACCOUNT_USER", "User endpoint");
        Require(backend.Writes.Last().Form["passwd2"] == "secret123", "Password confirmation");

        var createdReseller = accounts.CreateAsync(
            new("reseller", "reseller1", "reseller@example.com", "secret123",
                "reseller.example.com", "reseller-starter"), auth, CancellationToken.None)
            .GetAwaiter().GetResult();
        Require(createdReseller.Status == 200 && backend.Writes.Last().Path ==
            "CMD_API_ACCOUNT_RESELLER", "Reseller endpoint");

        var deleted = accounts.DeleteAsync("alice", "alice", auth, CancellationToken.None)
            .GetAwaiter().GetResult();
        Require(deleted.Status == 200, "Delete user");
        Require(backend.Writes.Last().Form["select0"] == "alice", "Exactly selected user");
        Require(backend.Writes.Last().Form["delete"] == "yes", "Delete flag");

        var count = backend.Writes.Count;
        var rejected = accounts.DeleteAsync("alice", "bob", auth, CancellationToken.None)
            .GetAwaiter().GetResult();
        Require(rejected.Status == 400 && backend.Writes.Count == count,
            "Reject unconfirmed delete locally, no upstream write");

        var zoneCreated = dns.CreateZoneAsync(new("example.com", "192.0.2.10",
            "ns1.example.com", "ns2.example.com"), auth, CancellationToken.None)
            .GetAwaiter().GetResult();
        Require(zoneCreated.Status == 200, "Create standalone DNS zone");
        Require(backend.Writes.Last().Path == "CMD_API_DNS_ADMIN" &&
            backend.Writes.Last().Form["action"] == "create" &&
            backend.Writes.Last().Form["ip"] == "192.0.2.10", "Create-zone exact form");

        var zoneDeleted = dns.DeleteZoneAsync("example.com", "example.com", auth,
            CancellationToken.None).GetAwaiter().GetResult();
        Require(zoneDeleted.Status == 200 && backend.Writes.Last().Form["action"] == "delete" &&
            backend.Writes.Last().Form["select0"] == "example.com", "Delete-zone exact form");

        count = backend.Writes.Count;
        var invalid = dns.DeleteZoneAsync("example.com", "different.com", auth,
            CancellationToken.None).GetAwaiter().GetResult();
        Require(invalid.Status == 400 && backend.Writes.Count == count,
            "Delete zone requires exact confirmation");

        backend.ResultBody = "error=1&text=Denied";
        var denied = accounts.DeleteAsync("alice", "alice", auth, CancellationToken.None)
            .GetAwaiter().GetResult();
        Require(denied.Status == 422 && denied.Error == "DIRECTADMIN_REJECTED_OPERATION",
            "Legacy HTTP 200 with error=1 must not be treated as success");
        backend.ResultBody = "error=0&text=OK";

        backend.Status = HttpStatusCode.Forbidden;
        denied = accounts.DeleteAsync("alice", "alice", auth, CancellationToken.None)
            .GetAwaiter().GetResult();
        Require(denied.Status == 403 && denied.Error is not null,
            "Backend permission denial must propagate");
        backend.Status = HttpStatusCode.Redirect;
        denied = accounts.DeleteAsync("alice", "alice", auth, CancellationToken.None)
            .GetAwaiter().GetResult();
        Require(denied.Status == 302 && denied.Error is not null,
            "Redirect must not be treated as success");

        // This test client intentionally never makes a network request.
    }

    private static AuthenticationHeaderValue Basic(string user, string password) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password)));

    private static void Require(bool condition, string reason)
    { if (!condition) throw new InvalidOperationException(reason); }

    private sealed class FakeFactory(FakeBackend handler) : IHttpClientFactory
    {
        private readonly HttpClient client = new(handler) {
            BaseAddress = new Uri("https://directadmin.invalid:2222/") };
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class FakeBackend : HttpMessageHandler
    {
        public readonly List<(string Path, Dictionary<string, string> Form)> Writes = [];
        public HttpStatusCode Status = HttpStatusCode.OK;
        public string ResultBody = "error=0&text=OK";
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var form = request.Content is null ? "" :
                await request.Content.ReadAsStringAsync(cancellationToken);
            var fields = System.Web.HttpUtility.ParseQueryString(form);
            var parsed = fields.AllKeys.Where(k => k is not null)
                .ToDictionary(k => k!, k => fields[k!] ?? "");
            Writes.Add((request.RequestUri!.AbsolutePath.TrimStart('/'), parsed));
            return new HttpResponseMessage(Status) {
                Content = new StringContent(ResultBody, Encoding.UTF8, "text/plain")
            };
        }
    }
}
