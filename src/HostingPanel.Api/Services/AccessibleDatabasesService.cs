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
{
    public async Task<AccessibleDatabaseInventory> ListAsync(
        AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var listed = await accounts.ListAsync(auth, ct);
        if (listed.Accounts is null)
            return new(listed.Status, null, null, listed.Error);

        if (!ImpersonationService.TryDecode(auth, out var principal, out var password))
            return new(401, null, null, "INVALID_CREDENTIALS");

        var results = new List<OwnedDatabase>();
        var warnings = new List<DatabaseInventoryWarning>();
        foreach (var account in listed.Accounts.DistinctBy(x => x.Username, StringComparer.OrdinalIgnoreCase))
        {
            var username = account.Username;
            AuthenticationHeaderValue effectiveAuth = auth;
            if (!username.Equals(principal, StringComparison.OrdinalIgnoreCase))
            {
                var check = await impersonation.CheckAsync(auth, username, ct);
                if (check.Status != 200)
                {
                    warnings.Add(new(username, check.Status, check.Error ?? "DELEGATION_UNAVAILABLE"));
                    continue;
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
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode)
                {
                    warnings.Add(new(username, (int)response.StatusCode, "UPSTREAM_DATABASE_LIST_UNAVAILABLE"));
                    continue;
                }
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                if (json.RootElement.ValueKind != JsonValueKind.Array)
                {
                    warnings.Add(new(username, 502, "INVALID_DATABASE_LIST"));
                    continue;
                }
                foreach (var database in json.RootElement.EnumerateArray())
                    results.Add(new(username, database.Clone()));
            }
            catch (HttpRequestException)
            { warnings.Add(new(username, 502, "UPSTREAM_UNAVAILABLE")); }
            catch (JsonException)
            { warnings.Add(new(username, 502, "INVALID_DATABASE_LIST")); }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            { warnings.Add(new(username, 504, "UPSTREAM_TIMEOUT")); }
        }

        return new(200, results, warnings, warnings.Count == 0 ? null : "PARTIAL_UPSTREAM_RESULTS");
    }
}

public sealed record OwnedDatabase(string Owner, JsonElement Database);
public sealed record DatabaseInventoryWarning(string Account, int Status, string Reason);
public sealed record AccessibleDatabaseInventory(int Status, IReadOnlyList<OwnedDatabase>? Items,
    IReadOnlyList<DatabaseInventoryWarning>? Warnings, string? Error);
