using HostingPanel.Web.Services;
using HostingPanel.Api.Adapters;

var checks = new List<(string Description, Action Test)>
{
    ("User list JSON is parsed", () =>
    {
        var users = DirectAdminUsersParser.Parse("[\"admin\",\"demo\"]");
        Require(users.Count == 2 && users[0] == "admin" && users[1] == "demo");
    }),
    ("Legacy adapter converts modern search response", () =>
    {
        var names = LegacyUsersAdapter.ParseUsernames("[\"demo\",\"admin\",\"demoadmin\"]");
        var filtered = LegacyUsersAdapter.SearchUsers(names, "demo", 1);
        Require(filtered.Count == 1 && filtered[0] == "demo");
    }),
    ("Legacy adapter rejects error responses", () =>
        Throws<LegacyApiException>(() => LegacyUsersAdapter.ParseUsernames("{\"error\":1,\"text\":\"Denied\"}"))),
    ("Legacy adapter rejects invalid schema", () =>
        Throws<FormatException>(() => LegacyUsersAdapter.ParseUsernames("{\"users\":[\"admin\"]}"))),
    ("Legacy adapter validates search limits", () =>
        Throws<ArgumentOutOfRangeException>(() => LegacyUsersAdapter.SearchUsers(["admin"], null, 0))),
    ("Legacy detail parser preserves URL-decoded fields", () =>
    {
        var v = LegacyUserDetailsAdapter.Parse("username=demo&email=a%2Bb%40example.net&ssh=ON");
        Require(v["email"] == "a+b@example.net" && v["ssh"] == "ON");
    }),
    ("Legacy config maps field types without inventing other fields", () =>
    {
        var parsed = LegacyUserDetailsAdapter.Parse("username=demo&usertype=user&ssh=ON&suspended=no&quota=unlimited&nemails=5");
        var config = LegacyUserDetailsAdapter.ConvertConfig(parsed);
        Require(config["ssh"]!.GetValue<bool>() == true);
        Require(config["suspended"]!.GetValue<bool>() == false);
        Require(config["emailAccountsLim"]!.GetValue<long>() == 5);
        Require(!config.ContainsKey("wordpress"));
        Require(LegacyUserDetailsAdapter.MissingRequired(config, ["wordpress"]).Length == 1);
    }),
    ("Legacy usage converts MiB accurately", () =>
    {
        var used = LegacyUserDetailsAdapter.Parse("bandwidth=1.5&quota=20&nemails=2");
        var limits = LegacyUserDetailsAdapter.Parse("bandwidth=unlimited&quota=50&nemails=5");
        var usage = LegacyUserDetailsAdapter.ConvertUsage(used, limits);
        Require(usage["bandwidthBytes"]!["usage"]!.GetValue<long>() == 1572864L);
        Require(usage["bandwidthBytes"]!["unlimited"]!.GetValue<bool>());
        Require(usage["quotaBytes"]!["limit"]!.GetValue<long>() == 52428800L);
    }),
    ("Domain fixtures accept only valid xyz1 test domains", () =>
    {
        Require(LegacyDomainsAdapter.IsSafeTestDomain("hosting-test.xyz1"));
        Require(LegacyDomainsAdapter.IsSafeTestDomain("web.demo-01.xyz1"));
        Require(!LegacyDomainsAdapter.IsSafeTestDomain("example.com"));
        Require(!LegacyDomainsAdapter.IsSafeTestDomain("bad..xyz1"));
        Require(!LegacyDomainsAdapter.IsSafeTestDomain("-bad.xyz1"));
    }),
    ("Legacy indexed domain list is normalized", () =>
    {
        var domains = LegacyDomainsAdapter.ParseDomains("list%5B%5D=web.demo.xyz1&list%5B%5D=hosting-test.xyz1");
        Require(domains.Count == 2 && domains[0] == "hosting-test.xyz1");
    }),
    ("Account domain-keyed response is parsed", () =>
    {
        var domains = LegacyDomainsAdapter.ParseDomains(
            "site-one.xyz1=123%3Aunlimited%3A0%3A3%3A0%3Ano%3Aunlimited%3AON%3AOFF%3AON&site-two.xyz1=42%3A100");
        Require(domains.SequenceEqual(["site-one.xyz1","site-two.xyz1"]));
    }),
    ("Domain JSON fixtures are parsed", () =>
    {
        var domains = LegacyDomainsAdapter.ParseDomains("""["hosting-test.xyz1","web.demo.xyz1"]""");
        Require(domains.Count == 2);
    }),
    ("Domain creation preview retains safe fixed form", () =>
    {
        var form = LegacyDomainsAdapter.EncodeCreateForm("hosting-test.xyz1", true, true, false);
        Require(form.Contains("action=create") && form.Contains("domain=hosting-test.xyz1") &&
                form.Contains("php=ON") && form.Contains("cgi=OFF"));
    }),
    ("Empty list is valid", () => Require(DirectAdminUsersParser.Parse("[]").Count == 0)),
    ("Legacy error object is rejected", () =>
        Throws<InvalidOperationException>(() => DirectAdminUsersParser.Parse("{\"error\":1,\"text\":\"Access denied\"}"))),
    ("Malformed response is rejected", () => Throws<System.Text.Json.JsonException>(() => DirectAdminUsersParser.Parse("error=1"))),
    ("Wrong JSON structure is rejected", () => Throws<FormatException>(() => DirectAdminUsersParser.Parse("\"admin\""))),
    ("Only modern API endpoints are accepted", () =>
    {
        Require(!DirectAdminSession.IsAllowedEndpoint("CMD_API_SHOW_ALL_USERS?json=yes"));
        Require(DirectAdminSession.IsAllowedEndpoint("api/db-show/databases"));
    }),
    ("Unsafe and non-API paths are rejected", () =>
    {
        foreach (var path in new[] { "/CMD_API_SHOW_ALL_USERS", "https://evil.example/a",
            "../CMD_API_SHOW_ALL_USERS", "api/../../secret", "api/x#fragment",
            "not-an-api", "api/\\foo" })
            Require(!DirectAdminSession.IsAllowedEndpoint(path));
    })
};

var failed = 0;
foreach (var (description, test) in checks)
{
    try { test(); Console.WriteLine("PASS " + description); }
    catch (Exception error) { failed++; Console.Error.WriteLine("FAIL " + description + ": " + error.Message); }
}

Console.WriteLine($"{checks.Count - failed}/{checks.Count} tests passed.");
return failed == 0 ? 0 : 1;

static void Require(bool condition)
{
    if (!condition) throw new Exception("Assertion failed.");
}
static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
