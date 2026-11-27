using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using HostingPanel.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace HostingPanel.Api;

internal sealed class StalwartMailProvider(HostingPanelDbContext db, KubernetesIdentityService identity,
    IHttpClientFactory clients, IConfiguration configuration, MailMutationGate gate, KubernetesOperationQueue operations, HostingOperationNotifications? notifications = null) : IMailProvider, IMailForwarders
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
        Func<HttpClient, string, Task<MailResult>> action, bool createDomain = false, bool mutation = false, bool forwarder = false)
    {
        if (mutation) await gate.Semaphore.WaitAsync(ct);
        try { return await ExecuteCore(domain, auth, ct, action, createDomain, mutation, forwarder); }
        finally { if (mutation) gate.Semaphore.Release(); }
    }

    private async Task<MailResult> ExecuteCore(string domain, AuthenticationHeaderValue auth, CancellationToken ct,
        Func<HttpClient, string, Task<MailResult>> action, bool createDomain, bool mutation, bool forwarder)
    {
        var principal = await identity.AuthenticateAsync(auth, ct);
        if (principal is null) return new(401, "UNAUTHORIZED");
        var row = await db.Domains.AsNoTracking().SingleOrDefaultAsync(d => d.Name == domain.ToLowerInvariant(), ct);
        if (row is null || row.Suspended) return new(404, "DOMAIN_NOT_FOUND");
        var owner = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == row.OwnerId, ct);
        if (owner is null || owner.Suspended) return new(404, "DOMAIN_NOT_FOUND");
        var visible = await identity.VisibleAccountsAsync(principal, ct);
        if (!visible.Contains(row.OwnerId)) return new(404, "DOMAIN_NOT_FOUND");
        // Until package limits and domain lifecycle are integrated, mutations are operator-only.
        if (mutation && principal.Kind != "admin") return new(403, "MAIL_MUTATION_ADMIN_ONLY");
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
            if (createDomain)
            {
                var package = await db.Packages.AsNoTracking().Where(p => p.Id == owner.PackageId).Select(p => p.LimitsJson).SingleOrDefaultAsync(ct);
                var limit = MailLimitPolicy.Resolve(package, owner.LimitsJson, forwarder ? "nemailf" : "nemails");
                if (limit == 0) return new(409, "MAIL_ACCOUNT_LIMIT_REACHED");
                if (limit != long.MaxValue)
                {
                    var names = await db.Domains.AsNoTracking().Where(d => d.OwnerId == owner.Id).Select(d => d.Name).ToListAsync(ct);
                    long count = 0;
                    foreach (var name in names)
                    {
                        var matchesForOwner = (await Query(client, "Domain", new { name }, new[] { "id", "name" }, ct)).Where(d => Field(d, "name") == name).ToArray();
                        if (matchesForOwner.Length == 0) continue;
                        if (matchesForOwner.Length != 1 || Field(matchesForOwner[0], "id") is not { } ownedId)
                            return new(502, "MAIL_INVENTORY_UNVERIFIED");
                        var inventory = forwarder ? await Lists(client, ownedId, ct) : await Query(client, "Account", new { domainId = ownedId }, new[] { "id", "@type", "domainId" }, ct);
                        if (inventory.Any(a => Field(a, "domainId") != ownedId || !forwarder && Field(a, "@type") is null))
                            return new(502, "MAIL_INVENTORY_UNVERIFIED");
                        count += inventory.LongCount(a => forwarder || Field(a, "@type") == "User");
                        if (!MailLimitPolicy.CanCreate(count, limit)) return new(409, "MAIL_ACCOUNT_LIMIT_REACHED");
                    }
                }
            }
            var domains = await Query(client, "Domain", new { name = row.Name }, new[] { "id", "name" }, ct);
            var matches = domains.Where(d => Field(d, "name") == row.Name).ToArray();
            if (matches.Length > 1) return new(502, "MAIL_UPSTREAM_ERROR");
            var domainId = matches.Length == 1 ? Field(matches[0], "id") : null;
            // Persist the DNS intent before any external domain creation. An ambiguous
            // upstream outcome must still be discoverable by reconciliation.
            if (createDomain && configuration.GetValue<bool>("PowerDns:Enabled") && configuration.GetValue("Stalwart:AutoProvisionDns", true))
            {
                if (!await db.ReconciliationOperations.AnyAsync(x => x.ResourceType == "mail-dns" && x.ResourceId == row.Id && x.Status == "pending", ct))
                {
                    operations.Schedule("mail-dns", row.Id, "update", new { row.Name });
                    await db.SaveChangesAsync(ct);
                    notifications?.PublishChange(ReconciliationTarget.Dns);
                }
            }
            if (domainId is null && createDomain)
            {
                var response = await Call(client, "x:Domain/set", new { create = new { item = new { name = row.Name } } }, ct);
                domainId = CreatedId(response);
                if (domainId is null) return new(409, "MAIL_DOMAIN_CREATE_FAILED");
            }
            if (domainId is null) return mutation ? new(404, "MAIL_DOMAIN_NOT_FOUND") : new(200, Accounts: Array.Empty<MailAccount>());
            return await action(client, domainId);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(503, "MAIL_UPSTREAM_TIMEOUT"); }
        catch (HttpRequestException) { return new(502, "MAIL_UPSTREAM_ERROR"); }
        catch (JsonException) { return new(502, "MAIL_UPSTREAM_ERROR"); }
        catch (InvalidOperationException) { return new(502, "MAIL_UPSTREAM_ERROR"); }
        catch (KeyNotFoundException) { return new(502, "MAIL_UPSTREAM_ERROR"); }
    }

    internal static async Task<JsonElement> Call(HttpClient client, string method, object arguments, CancellationToken ct)
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

    internal static async Task<List<JsonElement>> Query(HttpClient client, string type, object filter, string[] properties, CancellationToken ct)
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
            var batch = get.GetProperty("list").EnumerateArray().ToArray();
            if (batch.Length != ids.Length || !batch.Select(a => Field(a, "id")).ToHashSet().SetEquals(ids))
                throw new InvalidOperationException("Incomplete JMAP inventory.");
            results.AddRange(batch);
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
        (await Query(client, "Account", new { domainId }, new[] { "id", "@type", "domainId", "name", "emailAddress", "roles", "permissions", "quotas", "usedDiskQuota", "aliases" }, ct))
        .Where(a => Field(a, "domainId") == domainId && Field(a, "@type") == "User"
            && a.TryGetProperty("roles", out var roles) && Field(roles, "@type") == "User"
            && a.TryGetProperty("permissions", out var permissions) && Field(permissions, "@type") == "Inherit").ToList();

    private static IReadOnlyList<string> LocalAliases(JsonElement account, string domainId) =>
        account.TryGetProperty("aliases", out var aliases) && aliases.ValueKind == JsonValueKind.Object
            ? aliases.EnumerateObject().Where(a => Field(a.Value, "domainId") == domainId &&
                (!a.Value.TryGetProperty("enable", out var enabled) || enabled.ValueKind != JsonValueKind.False))
                .Select(a => Field(a.Value, "name")).OfType<string>().ToArray()
            : [];

    public Task<MailResult> ListAsync(string domain, AuthenticationHeaderValue auth, CancellationToken ct) =>
        Execute(domain, auth, ct, async (client, id) => new(200, Accounts: (await Accounts(client, id, ct)).Select(a =>
            new MailAccount(Field(a, "name")!, Field(a, "emailAddress")!,
                a.TryGetProperty("quotas", out var quotas) && quotas.ValueKind == JsonValueKind.Object ? Number(quotas, "maxDiskQuota") : null,
                Number(a, "usedDiskQuota"), LocalAliases(a, id))).ToArray()));

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
        }, createDomain: ValidName(input.Name) && ValidPassword(input.Password) && input.QuotaBytes > 0, mutation: true);

    public Task<MailResult> UpdateAsync(string domain, string name, MailUpdateRequest input, AuthenticationHeaderValue auth, CancellationToken ct) =>
        Execute(domain, auth, ct, async (client, domainId) =>
        {
            if (!ValidName(name) || input.Password is not null && !ValidPassword(input.Password)
                || input.QuotaBytes is <= 0 || input.Password is null && input.QuotaBytes is null && input.Aliases is null) return new(400, "INVALID_MAIL_ACCOUNT");
            var account = (await Accounts(client, domainId, ct)).SingleOrDefault(a => Field(a, "name") == name);
            if (account.ValueKind == JsonValueKind.Undefined || Field(account, "id") is not { } id) return new(404, "MAIL_ACCOUNT_NOT_FOUND");
            var patch = new Dictionary<string, object>();
            if (input.Password is not null)
            {
                var response = await Call(client, "x:Account/get", new { ids = new[] { id }, properties = new[] { "id", "credentials" } }, ct);
                var credentials = response.GetProperty("list").EnumerateArray().Single();
                if (Field(credentials, "id") != id) return new(502, "MAIL_INVENTORY_UNVERIFIED");
                if (!credentials.TryGetProperty("credentials", out var entries) || entries.ValueKind != JsonValueKind.Object)
                    return new(409, "MAIL_PASSWORD_CREDENTIAL_UNAVAILABLE");
                var passwords = entries.EnumerateObject().Where(x => Field(x.Value, "@type") == "Password").ToArray();
                if (passwords.Length != 1 || !passwords[0].Name.All(char.IsAsciiDigit))
                    return new(409, "MAIL_PASSWORD_CREDENTIAL_UNAVAILABLE");
                patch["credentials/" + passwords[0].Name + "/secret"] = input.Password;
            }
            if (input.QuotaBytes is long quota) patch["quotas/maxDiskQuota"] = quota;
            if (input.Aliases is not null)
            {
                if (input.Aliases.Count > 100 || input.Aliases.Any(a => !ValidName(a) || a == name)
                    || input.Aliases.Distinct(StringComparer.OrdinalIgnoreCase).Count() != input.Aliases.Count)
                    return new(400, "INVALID_MAIL_ALIASES");
                if (!account.TryGetProperty("aliases", out var aliases) || aliases.ValueKind != JsonValueKind.Object)
                    return new(502, "MAIL_INVENTORY_UNVERIFIED");
                // Never overwrite aliases in other domains or disabled addresses.
                var retained = aliases.EnumerateObject().Where(a => Field(a.Value, "domainId") != domainId ||
                    (a.Value.TryGetProperty("enable", out var enabled) && enabled.ValueKind == JsonValueKind.False)).ToArray();
                var desired = input.Aliases.ToHashSet(StringComparer.Ordinal);
                var used = aliases.EnumerateObject().Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
                foreach (var existing in aliases.EnumerateObject())
                {
                    if (retained.Any(a => a.Name == existing.Name)) continue;
                    if (Field(existing.Value, "name") is { } alias && desired.Remove(alias)) continue;
                    if (!existing.Name.All(char.IsAsciiDigit)) return new(502, "MAIL_INVENTORY_UNVERIFIED");
                    patch["aliases/" + existing.Name] = null!;
                }
                var next = 0;
                foreach (var alias in desired)
                {
                    while (used.Contains(next.ToString(System.Globalization.CultureInfo.InvariantCulture))) next++;
                    var key = (next++).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    patch["aliases/" + key] = new { name = alias, domainId };
                }
            }
            if (patch.Count == 0) return new(200);
            var result = await Call(client, "x:Account/set", new { update = new Dictionary<string, object> { [id] = patch } }, ct);
            return Updated(result, id) ? new(200) : new(409, "MAIL_UPDATE_FAILED");
        }, mutation: true);

    public Task<MailResult> DeleteAsync(string domain, string name, AuthenticationHeaderValue auth, CancellationToken ct) =>
        Execute(domain, auth, ct, async (client, domainId) =>
        {
            if (!ValidName(name)) return new(400, "INVALID_MAIL_ACCOUNT");
            var account = (await Accounts(client, domainId, ct)).SingleOrDefault(a => Field(a, "name") == name);
            if (account.ValueKind == JsonValueKind.Undefined || Field(account, "id") is not { } id) return new(404, "MAIL_ACCOUNT_NOT_FOUND");
            var result = await Call(client, "x:Account/set", new { destroy = new[] { id } }, ct);
            return result.TryGetProperty("destroyed", out var destroyed) && destroyed.ValueKind == JsonValueKind.Array
                && destroyed.EnumerateArray().Any(x => x.GetString() == id) ? new(200) : new(409, "MAIL_DELETE_FAILED");
        }, mutation: true);

    private static async Task<List<JsonElement>> Lists(HttpClient client, string domainId, CancellationToken ct) =>
        (await Query(client, "MailingList", new { }, new[] { "id", "name", "domainId", "recipients", "aliases" }, ct))
        .Where(x => Field(x, "domainId") == domainId).ToList();

    private static bool ValidForwarder(MailForwarder input, string domain) =>
        ValidName(input.Name) && input.Recipients is { Count: > 0 and <= 100 }
        && input.Recipients.All(x => x is { Length: > 0 and <= 254 }
            && System.Net.Mail.MailAddress.TryCreate(x, out var address) && address.Address == x
            && !x.Equals(input.Name + "@" + domain, StringComparison.OrdinalIgnoreCase))
        && input.Recipients.Distinct(StringComparer.OrdinalIgnoreCase).Count() == input.Recipients.Count;

    private static Dictionary<string, bool> RecipientSet(MailForwarder input) =>
        input.Recipients.ToDictionary(x => x, _ => true, StringComparer.OrdinalIgnoreCase);

    private async Task<MailForwarderResult> ForwarderOperation(string domain, AuthenticationHeaderValue auth,
        CancellationToken ct, Func<HttpClient, string, Task<MailForwarderResult>> operation,
        bool create = false, bool mutation = false)
    {
        MailForwarderResult? answer = null;
        var result = await Execute(domain, auth, ct, async (client, id) =>
        {
            answer = await operation(client, id);
            return new MailResult(answer.Status, answer.Error);
        }, createDomain: create, mutation: mutation, forwarder: true);
        return answer ?? new(result.Status, result.Error, result.Status == 200 ? Array.Empty<MailForwarder>() : null);
    }

    public Task<MailForwarderResult> ListForwardersAsync(string domain, AuthenticationHeaderValue auth, CancellationToken ct) =>
        ForwarderOperation(domain, auth, ct, async (client, id) => new(200, Forwarders:
            (await Lists(client, id, ct)).Select(x => new MailForwarder(
                Field(x, "name") ?? throw new InvalidOperationException("Missing list name."),
                x.GetProperty("recipients").EnumerateObject().Where(r => r.Value.ValueKind == JsonValueKind.True)
                    .Select(r => r.Name).ToArray())).ToArray()));

    public Task<MailForwarderResult> CreateForwarderAsync(string domain, MailForwarder input, AuthenticationHeaderValue auth, CancellationToken ct) =>
        !ValidForwarder(input, domain) ? Task.FromResult(new MailForwarderResult(400, "INVALID_FORWARDER"))
        : ForwarderOperation(domain, auth, ct, async (client, id) =>
        {
            var response = await Call(client, "x:MailingList/set", new { create = new { item = new {
                name = input.Name, domainId = id, recipients = RecipientSet(input) } } }, ct);
            return CreatedId(response) is null ? new(409, "FORWARDER_CREATE_FAILED") : new(201);
        }, create: true, mutation: true);

    public Task<MailForwarderResult> UpdateForwarderAsync(string domain, MailForwarder input, AuthenticationHeaderValue auth, CancellationToken ct) =>
        !ValidForwarder(input, domain) ? Task.FromResult(new MailForwarderResult(400, "INVALID_FORWARDER"))
        : ForwarderOperation(domain, auth, ct, async (client, domainId) =>
        {
            var row = (await Lists(client, domainId, ct)).SingleOrDefault(x => Field(x, "name") == input.Name);
            if (row.ValueKind == JsonValueKind.Undefined || Field(row, "id") is not { } id) return new(404, "FORWARDER_NOT_FOUND");
            var result = await Call(client, "x:MailingList/set", new { update = new Dictionary<string, object> {
                [id] = new { recipients = RecipientSet(input) } } }, ct);
            return Updated(result, id) ? new(200) : new(409, "FORWARDER_UPDATE_FAILED");
        }, mutation: true);

    public Task<MailForwarderResult> DeleteForwarderAsync(string domain, string name, AuthenticationHeaderValue auth, CancellationToken ct) =>
        !ValidName(name) ? Task.FromResult(new MailForwarderResult(400, "INVALID_FORWARDER"))
        : ForwarderOperation(domain, auth, ct, async (client, domainId) =>
        {
            var row = (await Lists(client, domainId, ct)).SingleOrDefault(x => Field(x, "name") == name);
            if (row.ValueKind == JsonValueKind.Undefined || Field(row, "id") is not { } id) return new(404, "FORWARDER_NOT_FOUND");
            if (!row.TryGetProperty("aliases", out var aliases) || aliases.ValueKind != JsonValueKind.Object || aliases.EnumerateObject().Any())
                return new(409, "FORWARDER_HAS_ALIASES");
            var result = await Call(client, "x:MailingList/set", new { destroy = new[] { id } }, ct);
            return result.TryGetProperty("destroyed", out var destroyed) && destroyed.ValueKind == JsonValueKind.Array
                && destroyed.EnumerateArray().Any(x => x.GetString() == id) ? new(200) : new(409, "FORWARDER_DELETE_FAILED");
        }, mutation: true);
}
