using HostingPanel.Web.Services;
using HostingPanel.Api.Adapters;
using HostingPanel.Api.Services;
using System.Net.Http.Headers;
using System.Text;

var checks = new List<(string Description, Action Test)>
{
    ("Web hosting capability is implemented by both providers", () =>
    {
        Require(typeof(HostingPanel.Api.IWebHosting).IsAssignableFrom(typeof(DomainManagementService)));
        Require(typeof(HostingPanel.Api.IWebHosting).IsAssignableFrom(typeof(HostingPanel.Api.KubernetesBackend)));
        Require(typeof(HostingPanel.Api.IHostingBackend).GetProperty("WebHosting")?.PropertyType == typeof(HostingPanel.Api.IWebHosting));
        var accepted = new HostingPanel.Api.WebsiteMutationResult(202, null);
        Require(accepted.ProvisioningPending && !accepted.Executed);
        var confirmed = new HostingPanel.Api.WebsiteMutationResult(200, null);
        Require(confirmed.Executed && !confirmed.ProvisioningPending);
        Require(!new HostingPanel.Api.WebsiteMutationResult(403, "ACCESS_DENIED").Executed);
    }),
    ("Provider assemblies do not reference each other or API", () =>
    {
        var contract = typeof(HostingPanel.Api.IHostingBackend).Assembly;
        var direct = typeof(HostingPanel.Api.DirectAdminBackend).Assembly;
        var kube = typeof(HostingPanel.Api.KubernetesBackend).Assembly;
        Require(contract != direct && contract != kube && direct != kube);
        Require(!contract.GetReferencedAssemblies().Any(a => a.Name!.StartsWith("HostingPanel.")));
        Require(!direct.GetReferencedAssemblies().Any(a => a.Name is "HostingPanel.Api" or "HostingPanel.Backends.Kubernetes"));
        Require(!kube.GetReferencedAssemblies().Any(a => a.Name is "HostingPanel.Api" or "HostingPanel.Backends.DirectAdmin"));
    }),
    ("Backend settings describe enum, validate and reject hidden or unknown fields", () =>
    {
        var settings = new HostingPanel.Api.KubernetesBackendSettings();
        var schema = HostingPanel.Api.BackendSettingsSchema.Describe("Kubernetes", settings);
        Require(schema.Fields.Count == 1 && schema.Fields[0].Type == "enum" && schema.Fields[0].Options.Count == 2);
        Require(schema.Values.GetProperty("IngressProvider").GetString() == "NginxIngress");
        using var good = System.Text.Json.JsonDocument.Parse("""{"IngressProvider":"EnvoyProxy"}""");
        Require(HostingPanel.Api.BackendSettingsSchema.TryRead<HostingPanel.Api.KubernetesBackendSettings>(good.RootElement, out var proposed, out _));
        Require(proposed!.IngressProvider == HostingPanel.Api.KubernetesIngressProvider.EnvoyProxy);
        Require(HostingPanel.Api.BackendSettingsSchema.Revision(settings) != HostingPanel.Api.BackendSettingsSchema.Revision(proposed));
        foreach (var invalid in new[] { """{"IngressProvider":1}""", """{"IngressProvider":"invalid"}""", """{"IngressProvider":"EnvoyProxy","Validator":{}}""", """{}""", """{"IngressProvider":"EnvoyProxy","IngressProvider":"NginxIngress"}""" })
        {
            using var document = System.Text.Json.JsonDocument.Parse(invalid);
            Require(!HostingPanel.Api.BackendSettingsSchema.TryRead<HostingPanel.Api.KubernetesBackendSettings>(document.RootElement, out _, out _));
        }
    }),
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
    ("Domain validator supports normal and xyz1 domains", () =>
    {
        Require(LegacyDomainsAdapter.IsValidDomain("hosting-test.xyz1"));
        Require(LegacyDomainsAdapter.IsValidDomain("web.demo-01.xyz1"));
        Require(LegacyDomainsAdapter.IsValidDomain("example.com"));
        Require(LegacyDomainsAdapter.IsValidDomain("customer.co.uk"));
        Require(!LegacyDomainsAdapter.IsValidDomain("bad..xyz1"));
        Require(!LegacyDomainsAdapter.IsValidDomain("-bad.xyz1"));
        Require(!LegacyDomainsAdapter.IsValidDomain("not-a-domain"));
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
    ("Domain modification form uses documented action", () =>
    {
        var body = LegacyDomainsAdapter.EncodeModifyForm("site-one.xyz1", true, false, false);
        Require(body.Contains("action=modify") && body.Contains("ssl=OFF"));
    }),
    ("Domain deletion requires confirmed selection", () =>
    {
        var body = LegacyDomainsAdapter.EncodeDeleteForm("site-two.xyz1");
        Require(body == "delete=yes&confirmed=yes&select0=site-two.xyz1");
    }),
    ("DirectAdmin mutation responses must indicate success", () =>
    {
        LegacyDomainsAdapter.AssertMutationSucceeded("error=0&text=Success");
        Throws<LegacyApiException>(() => LegacyDomainsAdapter.AssertMutationSucceeded("error=1&text=Denied"));
        Throws<FormatException>(() => LegacyDomainsAdapter.AssertMutationSucceeded("text=Unknown"));
        Throws<FormatException>(() => LegacyDomainsAdapter.AssertMutationSucceeded(""));
    }),
    ("Domain detail view parses legacy fields", () =>
    {
        var settings = LegacyDomainDetailsAdapter.Parse(
            "domain=hosting-test.xyz1&username=demo&php=ON&ssl=OFF&quota=unlimited&ip=192.0.2.1");
        Require(settings["domain"] == "hosting-test.xyz1" && settings["php"] == "ON");
        Require(settings["quota"] == "unlimited");
    }),
    ("Domain detail update only changes selected field", () =>
    {
        var payload = LegacyDomainDetailsAdapter.EncodeSetting("hosting-test.xyz1", "ssl", "ON");
        Require(payload.Contains("only_affect=ssl") && payload.Contains("ssl=ON")
            && !payload.Contains("&php=") && !payload.Contains("&cgi="));
    }),
    ("Domain detail prevents unrelated writes", () =>
    {
        Require(!LegacyDomainDetailsAdapter.IsAllowedValue("password", "anything"));
        Require(!LegacyDomainDetailsAdapter.IsAllowedValue("quota", "-1"));
        Require(!LegacyDomainDetailsAdapter.IsAllowedValue("php", "ignored"));
        Require(LegacyDomainDetailsAdapter.IsAllowedValue("bandwidth", "unlimited"));
    }),
    ("Account package overview filters credential-like keys", () =>
    {
        var fields = LegacyUserDetailsAdapter.Parse(
            "username=demo&package=starter&bandwidth=2000&password=hidden&login_key=hidden&ssh=ON");
        var filtered = LegacyAccountOverviewService.Redact(fields);
        Require(filtered["package"] == "starter" && filtered["bandwidth"] == "2000");
        Require(!filtered.ContainsKey("password") && !filtered.ContainsKey("login_key"));
    }),
    ("Account overview validates username", () =>
    {
        Require(LegacyAccountOverviewService.ValidUsername("customer_01"));
        Require(!LegacyAccountOverviewService.ValidUsername("../root"));
    }),
    ("DirectAdmin DNS JSON zone parses multiple record types", () =>
    {
        var entries = LegacyDnsAdapter.ParseRecords("""{"records":[{"type":"A","index":"www","value":"192.0.2.3","ttl":300},{"type":"TXT","index":"_acme-challenge","value":"test-token"}]}""");
        Require(entries.Count == 2 && entries[0].Name == "www" && entries[0].Ttl == "300");
        Require(entries[1].Type == "TXT");
    }),
    ("DNS add form uses only exact DNS fields", () =>
    {
        var form = LegacyDnsAdapter.Form("hosting-test.xyz1",
            new DnsChange("add", new DnsRecord("AAAA","www","2001:db8::1","300")));
        Require(form["action"] == "add" && form["type"] == "AAAA");
        Require(!form.Keys.Any(k => k.EndsWith("recs0")));
    }),
    ("DNS delete selects one exact value", () =>
    {
        var previous = new DnsRecord("TXT","_acme-challenge","test-token");
        var form = LegacyDnsAdapter.Form("hosting-test.xyz1",
            new DnsChange("delete",previous,previous));
        Require(form["action"] == "select");
        Require(form["txtrecs0"] == "name=_acme-challenge&value=test-token");
        Require(!form.ContainsKey("value"));
        var unsafeRecord = previous with { Value = "token=&value=other" };
        Throws<ArgumentException>(() => LegacyDnsAdapter.Form(
            "hosting-test.xyz1", new DnsChange("delete", unsafeRecord,unsafeRecord)));
    }),
    ("DNS edits require existing record and observe exact delta", () =>
    {
        var old = new DnsRecord("A","www","192.0.2.1");
        var newer = new DnsRecord("A","www","192.0.2.2");
        var form = LegacyDnsAdapter.Form("hosting-test.xyz1",new DnsChange("edit",newer,old));
        Require(form["action"] == "edit" && form["arecs0"] == "name=www&value=192.0.2.1");
        Require(LegacyDnsAdapter.Applied([old],[newer],new DnsChange("edit",newer,old)));
        Require(!LegacyDnsAdapter.Applied([old],[old],new DnsChange("edit",newer,old)));
    }),
    ("DNS validation rejects malformed record addresses and TTLs", () =>
    {
        Require(!LegacyDnsAdapter.Valid(new DnsRecord("A","www","not-an-ip")));
        Require(!LegacyDnsAdapter.Valid(new DnsRecord("A","www","192.0.2.1","2")));
        Require(!LegacyDnsAdapter.Valid(new DnsRecord("TXT","name&value=bad","hello")));
        Require(LegacyDnsAdapter.Valid(new DnsRecord("MX","@","10 mail.example.com.")));
    }),
    ("Impersonation accepts only original Basic credentials, never nested delegation", () =>
    {
        var valid = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:my-key")));
        Require(ImpersonationService.TryDecode(valid, out var who, out var secret));
        Require(who == "admin" && secret == "my-key");
        var delegated = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes("admin|victim:my-key")));
        Require(!ImpersonationService.TryDecode(delegated, out _, out _));
    }),
    ("Impersonation client refuses malformed account names", () =>
    {
        var session = new DirectAdminSession(null!, new HostingPanel.Web.Realtime.TicketChannelRegistry());
        Throws<ArgumentException>(() => session.SetCredentials("admin|victim", "secret"));
        session.SetCredentials("admin", "secret");
        Require(session.Username == "admin" && !session.IsImpersonating);
    }),
    ("Admin DNS list includes all zones independent of hosting owner", () =>
    {
        var zones = AdminDnsZonesAdapter.Parse(
            """{"0":{"domain":"hosting-test.xyz1","local_data":"yes","local_mail":"no"},"1":{"domain":"customer-test.xyz1","local_data":"yes","local_mail":"yes"},"info":{"dns_ns":"2"}}""");
        Require(zones.Count == 2);
        Require(zones[0].Domain == "customer-test.xyz1" && zones[0].LocalMail == true);
        Require(zones[1].Domain == "hosting-test.xyz1" && zones[1].LocalMail == false);
    }),
    ("Admin DNS list rejects invalid domains", () =>
        Throws<FormatException>(() => AdminDnsZonesAdapter.Parse(
            """{"0":{"domain":"bad..xyz1"}}"""))),
    ("Legacy account lists accept JSON and URL encoded arrays", () =>
    {
        Require(LegacyAccountListParser.Parse("""["admin","reseller1","user1"]""")
            .SequenceEqual(["admin","reseller1","user1"]));
        Require(LegacyAccountListParser.Parse("list%5B%5D=reseller1&list%5B%5D=user1")
            .SequenceEqual(["reseller1","user1"]));
        Require(LegacyAccountListParser.Parse("""{"list":["reseller1","user1"]}""")
            .Count == 2);
    }),
    ("Backend-denied account discovery must not be interpreted as an empty list", () =>
    {
        Throws<LegacyApiException>(() => LegacyAccountListParser.Parse("error=1&text=denied"));
        Throws<LegacyApiException>(() => LegacyAccountListParser.Parse("""{"error":"1"}"""));
    }),
    ("Legacy account list rejects invalid account names", () =>
        Throws<FormatException>(() => LegacyAccountListParser.Parse("""["valid","../invalid"]"""))),
    ("All pinned Swagger routes have exactly one specialized or generic controller owner", SwaggerRouteContractTests.Run),
    ("Blazor sidebar mirrors index.html navigation and every entry has a destination", NavigationParityContractTests.Run),
    ("Ticket backend and SignalR channel isolation with fake DirectAdmin", TicketIntegrationTests.Run),
    ("Backend is the sole source of admin reseller and enduser scopes", ScopeContractTests.Run),
    ("DNS forwards without role selection or read-gated writes", DnsForwardingContractTests.Run),
    ("Documented user/reseller and DNS mutations use exact forms and backend decisions", BackendMutationIntegrationTests.Run),
    ("Hierarchical databases respect backend delegation and mark denied children incomplete", DatabaseInventoryIntegrationTests.Run),
    ("Database legacy list parses URL encoded and JSON fixtures", () =>
    {
        var form=LegacyDatabaseAdapter.ParseNames("list%5B%5D=demo_app&list%5B%5D=demo_test");
        Require(form.SequenceEqual(["demo_app","demo_test"]));
        var json=LegacyDatabaseAdapter.ParseNames("""["demo_app","demo_test"]""");
        Require(json.SequenceEqual(form));
    }),
    ("Database create never serializes unrelated fields", () =>
    {
        var form=LegacyDatabaseAdapter.CreateForm("app","app","sample-password");
        Require(form["action"]=="create" && form["name"]=="app" &&
            form["user"]=="app" && form["passwd"]==form["passwd2"]);
        Require(form.Count==5);
    }),
    ("Database delete selects only named database", () =>
    {
        var form=LegacyDatabaseAdapter.DeleteForm("demo_app");
        Require(form["action"]=="delete" && form["select0"]=="demo_app");
        Require(form.Count==2);
        Throws<ArgumentException>(()=>LegacyDatabaseAdapter.DeleteForm("../../mysql"));
    }),
    ("Database backend errors cannot be treated as success", () =>
    {
        LegacyDatabaseAdapter.RequireSuccess("error=0&text=done");
        Throws<LegacyApiException>(()=>LegacyDatabaseAdapter.RequireSuccess("error=1&text=denied"));
        Throws<FormatException>(()=>LegacyDatabaseAdapter.RequireSuccess("text=done"));
    }),
    ("Database delete requires disappearance from live list", () =>
    {
        Require(LegacyDatabaseAdapter.Contains(["demo_app","demo_other"],
            ["demo_other"],"demo_app",false));
        Require(!LegacyDatabaseAdapter.Contains(["demo_app"],["demo_app"],"demo_app",false));
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
