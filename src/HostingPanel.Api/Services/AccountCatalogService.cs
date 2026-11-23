using System.Net;
using System.Net.Http.Headers;
using HostingPanel.Api.Adapters;

namespace HostingPanel.Api.Services;

public sealed record VisibleAccount(string Username, string Role, string? Creator);
public sealed record VisibleDomain(string Domain, string Owner, string OwnerRole);
public sealed record AccountCatalogResult(int Status, IReadOnlyList<VisibleAccount>? Accounts, string? Error);

/// <summary>
/// Discovery only: DirectAdmin itself decides which account lists are accessible.
/// No independent user tree authorization or inferred permission inheritance.
/// </summary>
public sealed class AccountCatalogService(IHttpClientFactory clients)
{
    private sealed record ListResult(int Status, IReadOnlyList<string>? Names);
    private async Task<ListResult> FetchListAsync(string path, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = auth;
        using var response = await clients.CreateClient("legacy-directadmin").SendAsync(request, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return new((int)response.StatusCode, null);
        if (!response.IsSuccessStatusCode) return new(502, null);
        var content = await response.Content.ReadAsStringAsync(ct);
        try { return new(200, LegacyAccountListParser.Parse(content)); }
        catch (LegacyApiException) { return new(403, null); }
    }

    public async Task<AccountCatalogResult> ListAsync(AuthenticationHeaderValue auth, CancellationToken ct)
    {
        if (!ImpersonationService.TryDecode(auth, out var loggedIn, out _))
        {
            // The validated delegated login remains protected by DirectAdmin.
            // We must not accept malformed credentials.
            var decoded = TryReadDelegatedUsername(auth);
            if (decoded is null) return new(401, null, "UNAUTHORIZED");
            loggedIn = decoded;
        }

        try
        {
            // Each operation is allowed or rejected by DirectAdmin. A 403 simply
            // means this caller cannot discover that category, not that a local
            // role-based filter should run.
            var own = new VisibleAccount(loggedIn, "user", null);
            using (var ownRequest = new HttpRequestMessage(HttpMethod.Get,
                "CMD_API_SHOW_USER_CONFIG?user=" + Uri.EscapeDataString(loggedIn)))
            {
                ownRequest.Headers.Authorization = auth;
                using var ownResponse = await clients.CreateClient("legacy-directadmin")
                    .SendAsync(ownRequest, ct);
                if (ownResponse.IsSuccessStatusCode)
                {
                    try
                    {
                        var fields = LegacyUserDetailsAdapter.Parse(
                            await ownResponse.Content.ReadAsStringAsync(ct));
                        own = new VisibleAccount(loggedIn,
                            fields.TryGetValue("usertype", out var role) ? role.ToLowerInvariant() : "user",
                            fields.TryGetValue("creator", out var creator) ? creator : null);
                    }
                    catch (LegacyApiException) { /* User config is optional discovery metadata. */ }
                }
                // Users may be denied CMD_API_SHOW_USER_CONFIG and still have
                // access to their own domains. Discovery must not fail here.
            }

            var accounts = new Dictionary<string, VisibleAccount>(StringComparer.OrdinalIgnoreCase)
                { [loggedIn] = own };
            foreach (var (path, role) in new (string Path, string Role)[] {
                ("CMD_API_SHOW_ADMINS", "admin"),
                ("CMD_API_SHOW_RESELLERS", "reseller"),
                ("CMD_API_SHOW_ALL_USERS", "user"),
                ("CMD_API_SHOW_USERS", "user") })
            {
                var listed = await FetchListAsync(path + "?json=yes", auth, ct);
                if (listed.Status is 401 or 403 or 404) continue;
                if (listed.Names is null)
                    return new(502, null, "ACCOUNT_LIST_UNAVAILABLE");
                foreach (var name in listed.Names)
                {
                    if (!accounts.ContainsKey(name))
                        accounts[name] = new VisibleAccount(name, role, null);
                }
            }

            // Creator expresses the DirectAdmin ownership relationship, not a
            // locally defined visibility restriction. Missing values stay null.
            foreach (var username in accounts.Keys.ToArray())
            {
                if (username.Equals(loggedIn, StringComparison.OrdinalIgnoreCase)) continue;
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    "CMD_API_SHOW_USER_CONFIG?user=" + Uri.EscapeDataString(username));
                request.Headers.Authorization = auth;
                using var response = await clients.CreateClient("legacy-directadmin")
                    .SendAsync(request, ct);
                if (!response.IsSuccessStatusCode) continue;
                try
                {
                    var config = LegacyUserDetailsAdapter.Parse(
                        await response.Content.ReadAsStringAsync(ct));
                    accounts[username] = accounts[username] with
                    {
                        Creator = config.TryGetValue("creator", out var creator) ? creator : null
                    };
                }
                catch (LegacyApiException) { /* Legacy account metadata unavailable */ }
                catch (FormatException) { /* Listing is still valid */ }
            }
            return new(200, accounts.Values.OrderBy(x => x.Role).ThenBy(x => x.Username).ToArray(), null);
        }
        catch (LegacyApiException) { return new(403, null, "ACCESS_DENIED"); }
        catch (FormatException) { return new(502, null, "INVALID_LEGACY_RESPONSE"); }
        catch (System.Text.Json.JsonException) { return new(502, null, "INVALID_LEGACY_RESPONSE"); }
        catch (HttpRequestException) { return new(502, null, "UPSTREAM_UNAVAILABLE"); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        { return new(504, null, "UPSTREAM_TIMEOUT"); }
    }

    private static string? TryReadDelegatedUsername(AuthenticationHeaderValue auth)
    {
        if (!auth.Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(auth.Parameter)) return null;
        try
        {
            var value = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(auth.Parameter));
            var colon = value.IndexOf(':');
            if (colon <= 0 || colon == value.Length - 1) return null;
            var name = value[..colon];
            var split = name.Split('|');
            return split.Length == 2 &&
                LegacyAccountOverviewService.ValidUsername(split[0]) &&
                LegacyAccountOverviewService.ValidUsername(split[1]) ? split[1] : null;
        }
        catch (FormatException) { return null; }
    }
}
