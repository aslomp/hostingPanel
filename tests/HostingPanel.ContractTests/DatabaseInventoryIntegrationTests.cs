using System.Net;
using System.Net.Http.Headers;
using System.Text;
using HostingPanel.Api.Services;

public static class DatabaseInventoryIntegrationTests
{
    public static void Run()
    {
        foreach (var (principal, expected) in new[] {
            ("admin", 3), ("reseller1", 2), ("alice", 1)
        })
        {
            var factory = new FakeFactory();
            var accounts = new AccountCatalogService(factory);
            var service = new AccessibleDatabasesService(
                accounts, new ImpersonationService(factory), factory);
            var result = service.ListAsync(Basic(principal), CancellationToken.None)
                .GetAwaiter().GetResult();
            Assert(result.Status == 200 && result.Items?.Count == expected,
                $"Database inventory for {principal}: expected {expected}, got {result.Items?.Count}");
            Assert(result.Warnings?.Count == 0, "Unexpected partial result");
            Assert(result.Items!.Any(x => x.Owner == principal), "Missing own database");
            if (principal == "admin")
                Assert(result.Items.Any(x => x.Owner == "alice"), "Missing delegated end-user");
        }
        var deniedFactory = new FakeFactory(denyAlice: true);
        var deniedService = new AccessibleDatabasesService(
            new AccountCatalogService(deniedFactory),
            new ImpersonationService(deniedFactory), deniedFactory);
        var partial = deniedService.ListAsync(Basic("admin"), CancellationToken.None)
            .GetAwaiter().GetResult();
        Assert(partial.Status == 200 && partial.Items?.Count == 2 &&
            partial.Warnings?.Any(x => x.Account == "alice" && x.Status == 403) == true,
            "Denied child must be a partial warning, not an empty authoritative result");
        Assert(partial.Error == "PARTIAL_UPSTREAM_RESULTS", "Incomplete inventory marker");
    }

    private static void Assert(bool condition, string reason)
    { if (!condition) throw new InvalidOperationException(reason); }

    private static AuthenticationHeaderValue Basic(string user) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":valid")));

    private sealed class FakeFactory(bool denyAlice = false) : IHttpClientFactory
    {
        private readonly HttpClient client = new(new FakeHandler(denyAlice)) {
            BaseAddress = new Uri("https://directadmin.invalid:2222/")
        };
        public HttpClient CreateClient(string _) => client;
    }

    private sealed class FakeHandler(bool denyAlice) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var encoded = request.Headers.Authorization?.Parameter ?? "";
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            var credentials = decoded.Split(':', 2);
            var identity = credentials[0].Split('|', 2);
            var principal = identity[0];
            var effective = identity.Length == 2 ? identity[1] : principal;
            if (credentials.Length < 2 || credentials[1] != "valid")
                return Response(HttpStatusCode.Unauthorized);
            var allowed = principal switch {
                "admin" => new[] { "admin", "reseller1", "alice" },
                "reseller1" => new[] { "reseller1", "alice" },
                _ => new[] { principal }
            };
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
            var requested = query["user"] ?? effective;

            if (path == "CMD_API_SHOW_DOMAINS")
                return Response(HttpStatusCode.OK, "[\""+effective+".example.com\"]");
            if (path == "CMD_API_SHOW_USER_CONFIG")
            {
                if (!allowed.Contains(requested)) return Response(HttpStatusCode.Forbidden);
                var type = requested switch { "admin" => "admin", "reseller1" => "reseller", _ => "user" };
                return Response(HttpStatusCode.OK, $"username={requested}&usertype={type}");
            }
            if (path == "CMD_API_SHOW_ADMINS")
                return principal == "admin"
                    ? Response(HttpStatusCode.OK, "[\"admin\"]")
                    : Response(HttpStatusCode.Forbidden);
            if (path == "CMD_API_SHOW_RESELLERS")
                return principal == "admin"
                    ? Response(HttpStatusCode.OK, "[\"reseller1\"]")
                    : Response(HttpStatusCode.Forbidden);
            if (path == "CMD_API_SHOW_ALL_USERS")
                return principal == "admin"
                    ? Response(HttpStatusCode.OK, "[\"alice\"]")
                    : Response(HttpStatusCode.Forbidden);
            if (path == "CMD_API_SHOW_USERS")
                return principal == "reseller1"
                    ? Response(HttpStatusCode.OK, "[\"alice\"]")
                    : Response(HttpStatusCode.Forbidden);
            if (path == "api/session")
                return (!allowed.Contains(effective) || (denyAlice && effective == "alice"))
                    ? Response(HttpStatusCode.Forbidden)
                    : Response(HttpStatusCode.OK,
                        "{\"realUsername\":\"" + principal + "\",\"effectiveUsername\":\"" + effective + "\"}");
            if (path == "api/db-show/databases")
                return (!allowed.Contains(effective) || (denyAlice && effective == "alice"))
                    ? Response(HttpStatusCode.Forbidden)
                    : Response(HttpStatusCode.OK,
                        "[{\"name\":\"" + effective + "_app\",\"size\":123}]");
            return Response(HttpStatusCode.NotFound);
        }
        private static Task<HttpResponseMessage> Response(HttpStatusCode status, string value = "") =>
            Task.FromResult(new HttpResponseMessage(status) {
                Content = new StringContent(value, Encoding.UTF8, "application/json")
            });
    }
}
