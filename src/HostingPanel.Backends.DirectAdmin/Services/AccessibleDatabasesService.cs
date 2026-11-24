using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace HostingPanel.Api.Services;

/// <summary>
/// Resource-owned database inventory: discover accounts, ask DirectAdmin to
/// validate each delegated identity, then request its database list. No
/// admin/reseller/enduser role filtering is performed by HostingPanel.
/// </summary>
public sealed class AccessibleDatabasesService(
    AccountCatalogService accounts,
    ImpersonationService impersonation,
    IHttpClientFactory clients)
: IAccessibleDatabases
{
    public async Task<AccessibleDatabaseInventory> ListAsync(
        AuthenticationHeaderValue auth, CancellationToken ct)
{
        var listed = await accounts.ListAsync(auth, ct);
        if (listed.Accounts is null)
            return new(listed.Status, null, null, listed.Error);

        if (!ImpersonationService.TryDecode(auth, out var principal, out var password))
            return new(401, null, null, "INVALID_CREDENTIALS");

        var results = new ConcurrentBag<OwnedDatabase>();
        var warnings = new ConcurrentBag<DatabaseInventoryWarning>();
        var accountsToQuery = listed.Accounts.DistinctBy(x => x.Username,
            StringComparer.OrdinalIgnoreCase);
        await Parallel.ForEachAsync(accountsToQuery, new ParallelOptions
        {
            MaxDegreeOfParallelism = 4,
            CancellationToken = ct
        }, async (account, token) =>
        {
            var username = account.Username;
            AuthenticationHeaderValue effectiveAuth = auth;
            if (!username.Equals(principal, StringComparison.OrdinalIgnoreCase))
            {
                var check = await impersonation.CheckAsync(auth, username, token);
                if (check.Status != 200)
                {
                    warnings.Add(new(username, check.Status, check.Error ?? "DELEGATION_UNAVAILABLE"));
                    return;
                }
                effectiveAuth = new AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(
                        principal + "|" + username + ":" + password)));
            }
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "api/db-show/databases?no-size=true");
                request.Headers.Authorization = effectiveAuth;
                using var response = await clients.CreateClient("legacy-directadmin")
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                if (!response.IsSuccessStatusCode)
                {
                    warnings.Add(new(username, (int)response.StatusCode, "UPSTREAM_DATABASE_LIST_UNAVAILABLE"));
                    return;
                }
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                if (json.RootElement.ValueKind != JsonValueKind.Array)
                {
                    warnings.Add(new(username, 502, "INVALID_DATABASE_LIST"));
                    return;
                }
                foreach (var database in json.RootElement.EnumerateArray())
                    results.Add(new(username, database.Clone()));
            }
            catch (HttpRequestException)
            { warnings.Add(new(username, 502, "UPSTREAM_UNAVAILABLE")); }
            catch (JsonException)
            { warnings.Add(new(username, 502, "INVALID_DATABASE_LIST")); }
            catch (TaskCanceledException) when (!token.IsCancellationRequested)
            { warnings.Add(new(username, 504, "UPSTREAM_TIMEOUT")); }
        });

        var sorted = results.OrderBy(x => x.Owner, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Database.ToString(), StringComparer.Ordinal).ToArray();
        var issues = warnings.OrderBy(x => x.Account, StringComparer.OrdinalIgnoreCase).ToArray();
        return new(200, sorted, issues, issues.Length == 0 ? null : "PARTIAL_UPSTREAM_RESULTS");
    }
}




