using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using HostingPanel.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace HostingPanel.Api;

internal sealed class StalwartMailProvider(HostingPanelDbContext db, KubernetesIdentityService identity,
    IHttpClientFactory clients, IConfiguration configuration) : IMailProvider
{
    private static bool ValidName(string? name) => name is { Length: > 0 and <= 64 }
        && Regex.IsMatch(name, @"^[a-z0-9][a-z0-9._+-]*$", RegexOptions.CultureInvariant)
        && !name.EndsWith('.') && !name.Contains("..");
    private static bool ValidPassword(string? password) => password is { Length: >= 12 and <= 256 };
    private static string? Field(JsonElement value, string key) =>
        value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
    private static long? Number(JsonElement value, string key) =>
        value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var n) ? n : null;

    private async Task<MailResult> Execute(string domain, AuthenticationHeaderValue auth, CancellationToken ct,
        Func<HttpClient, string, Task<MailResult>> action, bool createDomain = false)
    {
        var principal = await identity.AuthenticateAsync(auth, ct);
        if (principal is null) return new(401, "UNAUTHORIZED");
        var row = await db.Domains.AsNoTracking().SingleOrDefaultAsync(d => d.Name == domain.ToLowerInvariant(), ct);
        if (row is null || row.Suspended) return new(404, "DOMAIN_NOT_FOUND");
        var visible = await identity.VisibleAccountsAsync(principal, ct);
        if (!visible.Contains(row.OwnerId)) return new(404, "DOMAIN_NOT_FOUND");
        if (!configuration.GetValue<bool>("Stalwart:Enabled")) return new(501, "MAIL_NOT_CONFIGURED");
        var endpoint = configuration["Stalwart:BaseUrl"];
        var token = configuration["Stalwart:ApiKey"];
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || string.IsNullOrWhiteSpace(token)) return new(503, "MAIL_NOT_CONFIGURED");
        try
        {
            using var client = clients.CreateClient("stalwart-management");
            client.BaseAddress = new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var domains = await Query(client, "Domain", new { name = row.Name }, new[] { "id", "name" }, ct);
            var matches = domains.Where(d => Field(d, "name") == row.Name).ToArray();
            if (matches.Length > 1) return new(502, "MAIL_UPSTREAM_ERROR");
            var domainId = matches.Length == 1 ? Field(matches[0], "id") : null;
            if (domainId is null && createDomain)
            {
                var response = await Call(client, "x:Domain/set", new { create = new { item = new { name = row.Name } } }, ct);
                domainId = CreatedId(response);
                if (domainId is null) return new(409, "MAIL_DOMAIN_CREATE_FAILED");
            }
            if (domainId is null) return new(200, Accounts: Array.Empty<MailAccount>());
            return await action(client, domainId);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(503, "MAIL_UPSTREAM_TIMEOUT"); }
        catch (HttpRequestException) { return new(502, "MAIL_UPSTREAM_ERROR"); }
        catch (JsonException) { return new(502, "MAIL_UPSTREAM_ERROR"); }
        catch (InvalidOperationException) { return new(502, "MAIL_UPSTREAM_ERROR"); }
        catch (KeyNotFoundException) { return new(502, "MAIL_UPSTREAM_ERROR"); }
    }

    private static async Task<JsonElement> Call(HttpClient client, string method, object arguments, CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync("jmap/", new {
            @using = new[] { "urn:ietf:params:jmap:core", "urn:stalwart:jmap" },
            methodCalls = new object[][] { new object[] { method, arguments, "0" } }
        }, ct);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var result = document.RootElement.GetProperty("methodResponses")[0];
        if (result[0].GetString() != method || result[2].GetString() != "0")
            throw new InvalidOperationException("Unexpected JMAP response.");
        return result[1].Clone();
    }

    private static async Task<List<JsonElement>> Query(HttpClient client, string type, object filter, string[] properties, CancellationToken ct)
    {
        var results = new List<JsonElement>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var position = 0; ; )
        {
            var query = await Call(client, $"x:{type}/query", new { filter, position, limit = 100 }, ct);
            var ids = query.GetProperty("ids").EnumerateArray().Select(i => i.GetString()!).ToArray();
            if (ids.Length == 0) return results;
            if (ids.Any(id => !seen.Add(id))) throw new InvalidOperationException("Unstable query.");
            var get = await Call(client, $"x:{type}/get", new { ids, properties }, ct);
            results.AddRange(get.GetProperty("list").EnumerateArray());
            position += ids.Length;
            if (position > 10000) throw new InvalidOperationException("Query exceeds inventory limit.");
        }
    }

    private static string? CreatedId(JsonElement response) =>
        response.TryGetProperty("created", out var created) && created.ValueKind == JsonValueKind.Object
        && created.TryGetProperty("item", out var item) ? Field(item, "id") : null;
    private static bool Updated(JsonElement response, string id) =>
        response.TryGetProperty("updated", out var updated) && updated.ValueKind == JsonValueKind.Object
        && updated.TryGetProperty(id, out _);
    private static async Task<List<JsonElement>> Accounts(HttpClient client, string domainId, CancellationToken ct) =>
        (await Query(client, "Account", new { domainId }, new[] { "id", "@type", "domainId", "name", "emailAddress", "roles", "permissions", "quotas", "usedDiskQuota" }, ct))
        .Where(a => Field(a, "domainId") == domainId && Field(a, "@type") == "User"
            && a.TryGetProperty("roles", out var roles) && Field(roles, "@type") == "User"
            && a.TryGetProperty("permissions", out var permissions) && Field(permissions, "@type") == "Inherit").ToList();

    public Task<MailResult> ListAsync(string domain, AuthenticationHeaderValue auth, CancellationToken ct) =>
        Execute(domain, auth, ct, async (client, id) => new(200, Accounts: (await Accounts(client, id, ct)).Select(a =>
            new MailAccount(Field(a, "name")!, Field(a, "emailAddress")!,
                a.TryGetProperty("quotas", out var quotas) && quotas.ValueKind == JsonValueKind.Object ? Number(quotas, "maxDiskQuota") : null,
                Number(a, "usedDiskQuota"))).ToArray()));

    public Task<MailResult> CreateAsync(string domain, MailCreateRequest input, AuthenticationHeaderValue auth, CancellationToken ct) =>
        Execute(domain, auth, ct, async (client, id) =>
        {
            if (!ValidName(input.Name) || !ValidPassword(input.Password) || input.QuotaBytes <= 0) return new(400, "INVALID_MAIL_ACCOUNT");
            var item = new Dictionary<string, object> {
                ["@type"] = "User", ["name"] = input.Name, ["domainId"] = id,
                ["roles"] = new Dictionary<string, string> { ["@type"] = "User" },
                ["credentials"] = new Dictionary<string, object> { ["0"] = new Dictionary<string, string> { ["@type"] = "Password", ["secret"] = input.Password } },
                ["quotas"] = new { maxDiskQuota = input.QuotaBytes }
            };
            var result = await Call(client, "x:Account/set", new { create = new { item } }, ct);
            return CreatedId(result) is null ? new(409, "MAIL_CREATE_FAILED") : new(201);
        }, createDomain: ValidName(input.Name) && ValidPassword(input.Password) && input.QuotaBytes > 0);

    public Task<MailResult> UpdateAsync(string domain, string name, MailUpdateRequest input, AuthenticationHeaderValue auth, CancellationToken ct) =>
        Execute(domain, auth, ct, async (client, domainId) =>
        {
            if (!ValidName(name) || input.Password is not null && !ValidPassword(input.Password)
                || input.QuotaBytes is <= 0 || input.Password is null && input.QuotaBytes is null) return new(400, "INVALID_MAIL_ACCOUNT");
            var account = (await Accounts(client, domainId, ct)).SingleOrDefault(a => Field(a, "name") == name);
            if (account.ValueKind == JsonValueKind.Undefined || Field(account, "id") is not { } id) return new(404, "MAIL_ACCOUNT_NOT_FOUND");
            var patch = new Dictionary<string, object>();
            // Password rotation is intentionally deferred until credential indexes can be resolved safely.
            if (input.Password is not null) return new(501, "MAIL_PASSWORD_RESET_NOT_SUPPORTED");
            if (input.QuotaBytes is long quota) patch["quotas/maxDiskQuota"] = quota;
            var result = await Call(client, "x:Account/set", new { update = new Dictionary<string, object> { [id] = patch } }, ct);
            return Updated(result, id) ? new(200) : new(409, "MAIL_UPDATE_FAILED");
        });

    public Task<MailResult> DeleteAsync(string domain, string name, AuthenticationHeaderValue auth, CancellationToken ct) =>
        Execute(domain, auth, ct, async (client, domainId) =>
        {
            if (!ValidName(name)) return new(400, "INVALID_MAIL_ACCOUNT");
            var account = (await Accounts(client, domainId, ct)).SingleOrDefault(a => Field(a, "name") == name);
            if (account.ValueKind == JsonValueKind.Undefined || Field(account, "id") is not { } id) return new(404, "MAIL_ACCOUNT_NOT_FOUND");
            var result = await Call(client, "x:Account/set", new { destroy = new[] { id } }, ct);
            return result.TryGetProperty("destroyed", out var destroyed) && destroyed.ValueKind == JsonValueKind.Array
                && destroyed.EnumerateArray().Any(x => x.GetString() == id) ? new(200) : new(409, "MAIL_DELETE_FAILED");
        });
}
