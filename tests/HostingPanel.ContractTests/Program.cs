using HostingPanel.Web.Services;

var checks = new List<(string Description, Action Test)>
{
    ("User list JSON is parsed", () =>
    {
        var users = DirectAdminUsersParser.Parse("[\"admin\",\"demo\"]");
        Require(users.Count == 2 && users[0] == "admin" && users[1] == "demo");
    }),
    ("Empty list is valid", () => Require(DirectAdminUsersParser.Parse("[]").Count == 0)),
    ("Legacy error object is rejected", () =>
        Throws<InvalidOperationException>(() => DirectAdminUsersParser.Parse("{\"error\":1,\"text\":\"Access denied\"}"))),
    ("Malformed response is rejected", () => Throws<System.Text.Json.JsonException>(() => DirectAdminUsersParser.Parse("error=1"))),
    ("Wrong JSON structure is rejected", () => Throws<FormatException>(() => DirectAdminUsersParser.Parse("\"admin\""))),
    ("API endpoints are accepted", () =>
    {
        Require(DirectAdminSession.IsAllowedEndpoint("CMD_API_SHOW_ALL_USERS?json=yes"));
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
