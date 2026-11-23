using System.Net;
using System.Net.Http.Headers;
using System.Text;
using HostingPanel.Api.Services;

/// <summary>Deterministic backend permission matrix; no DirectAdmin server touched.</summary>
public static class ScopeContractTests
{
    public static void Run()
    {
        foreach (var (principal, expectedAccounts, expectedDomains) in new[] {
            ("admin", 4, 4),
            ("reseller1", 2, 2),
            ("alice", 1, 1)
        })
        {
            var factory = new FakeFactory();
            var accounts = new AccountCatalogService(factory);
            var domains = new AccessibleDomainsService(accounts, new DomainManagementService(factory));
            var auth = Basic(principal, "valid");
            var listing = accounts.ListAsync(auth, CancellationToken.None).GetAwaiter().GetResult();
            Assert(listing.Status == 200 && listing.Accounts?.Count == expectedAccounts,
                $"Account scope failed: {principal}");
            var result = domains.ListAsync(auth, CancellationToken.None).GetAwaiter().GetResult();
            Assert(result.Status == 200 && result.Items?.Count == expectedDomains,
                $"Domain scope failed: {principal}");
            Assert(result.Items!.All(x => x.Domain.EndsWith(".xyz1", StringComparison.Ordinal)),
                "Unexpected fixture domains");
            if (principal == "admin")
            {
                Assert(result.Items.Any(x => x.Owner == "reseller1"),
                    "Admin must see reseller-owned domains");
                Assert(result.Items.Any(x => x.Owner == "bob"),
                    "Admin must see all enduser domains");
            }
            else if (principal == "reseller1")
                Assert(result.Items.All(x => x.Owner is "reseller1" or "alice"),
                    "Reseller must only receive backend-authorized domains");
        }
        var invalid = new AccountCatalogService(new FakeFactory()).ListAsync(
            Basic("alice", "invalid"), CancellationToken.None).GetAwaiter().GetResult();
        Assert(invalid.Status == 401 && invalid.Accounts is null,
            "Invalid credentials must never produce a visible self-account.");
    }

    private static AuthenticationHeaderValue Basic(string user, string secret) =>
        new("Basic",Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + secret)));

    private static void Assert(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
    }

    private sealed class FakeFactory : IHttpClientFactory
    {
        private readonly HttpClient client = new(new Backend())
            { BaseAddress = new Uri("https://directadmin.invalid:2222/") };
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Backend : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(
                request.Headers.Authorization?.Parameter ?? ""));
            var separator = decoded.IndexOf(':');
            var actor = decoded[..separator];
            var secret = decoded[(separator + 1)..];
            if (secret != "valid") return Reply(HttpStatusCode.Unauthorized);

            var uri = request.RequestUri!;
            var path = uri.AbsolutePath.TrimStart('/');
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var requested = query["user"] ?? actor;

            var allowedAccounts = actor switch {
                "admin" => new[] { "admin", "reseller1", "alice", "bob" },
                "reseller1" => new[] { "reseller1", "alice" },
                "alice" => new[] { "alice" },
                _ => Array.Empty<string>()
            };
            if (path == "CMD_API_SHOW_DOMAINS")
                return Reply(HttpStatusCode.OK, "[\"" + actor + ".xyz1\"]");

            if (path == "CMD_API_SHOW_USER_CONFIG")
            {
                if (!allowedAccounts.Contains(requested)) return Reply(HttpStatusCode.Forbidden);
                var role = requested switch {
                    "admin" => "admin", "reseller1" => "reseller", _ => "user"
                };
                var creator = requested switch {
                    "reseller1" => "admin", "alice" => "reseller1", "bob" => "admin",
                    _ => ""
                };
                return Reply(HttpStatusCode.OK,
                    $"username={requested}&usertype={role}&creator={creator}");
            }
            if (path == "CMD_API_SHOW_USER_DOMAINS")
            {
                if (!allowedAccounts.Contains(requested)) return Reply(HttpStatusCode.Forbidden);
                return Reply(HttpStatusCode.OK, "[\"" + requested + ".xyz1\"]");
            }

            if (path == "CMD_API_SHOW_ADMINS")
                return actor == "admin" ? Reply(HttpStatusCode.OK,"[\"admin\"]")
                    : Reply(HttpStatusCode.Forbidden);
            if (path == "CMD_API_SHOW_RESELLERS")
                return actor == "admin" ? Reply(HttpStatusCode.OK,"[\"reseller1\"]")
                    : Reply(HttpStatusCode.Forbidden);
            if (path == "CMD_API_SHOW_ALL_USERS")
                return actor == "admin" ? Reply(HttpStatusCode.OK,"[\"alice\",\"bob\"]")
                    : Reply(HttpStatusCode.Forbidden);
            if (path == "CMD_API_SHOW_USERS")
                return actor == "reseller1" ? Reply(HttpStatusCode.OK,"[\"alice\"]")
                    : Reply(HttpStatusCode.Forbidden);
            return Reply(HttpStatusCode.NotFound);
        }

        private static Task<HttpResponseMessage> Reply(HttpStatusCode status, string content = "") =>
            Task.FromResult(new HttpResponseMessage(status)
                { Content = new StringContent(content,Encoding.UTF8,"text/plain") });
    }
}
