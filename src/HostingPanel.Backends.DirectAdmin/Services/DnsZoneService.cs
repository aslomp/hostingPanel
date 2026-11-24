using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using HostingPanel.Api.Adapters;

namespace HostingPanel.Api.Services;

public sealed class DnsZoneService(IHttpClientFactory clients, IAccessibleDomains? accessibleDomains = null)
: IDNSProvider
{
    public async Task<AccessibleDnsInventory> ListAccessibleZonesAsync(AuthenticationHeaderValue auth, CancellationToken ct)
    {
        if (accessibleDomains is null) return new(501, null, "DOMAIN_DISCOVERY_UNAVAILABLE");
        var zones = new Dictionary<string, AccessibleDnsZone>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        var adminStatus = 200;

        // DirectAdmin may host standalone DNS zones that have no hosting domain.
        // Discover those from the admin DNS interface, not just account domains.
        // No assumed admin/reseller role and no locally constructed permission tree.
        for (var page = 1; page <= 10000; page++)
        {
            var listed = await ListAdminZonesAsync(auth, page, ct);
            if (listed.Zones is null)
            {
                adminStatus = listed.Status;
                if (page > 1 || listed.Status is not (401 or 403 or 404))
                    warnings.Add("ADMIN_DNS_LIST_UNAVAILABLE");
                break;
            }
            if (listed.Zones.Count == 0) { break; }

            var fresh = 0;
            foreach (var zone in listed.Zones)
            {
                if (zones.TryAdd(zone.Domain, new AccessibleDnsZone(
                    zone.Domain, zone.LocalData, zone.LocalMail, null)))
                    fresh++;
            }

            if (fresh == 0)
            {
                // A legacy backend may ignore page= and repeat the same result.
                warnings.Add("ADMIN_DNS_PAGINATION_UNVERIFIED");
                break;
            }
            if (page == 10000)
                warnings.Add("ADMIN_DNS_PAGE_LIMIT");
        }

        // Supplement DNS-only results with resources discoverable through the
        // caller's accessible account hierarchy. Neither source is a role check.
        var accessible = await accessibleDomains.ListAsync(auth, ct);
        if (accessible.Items is not null)
        {
            foreach (var item in accessible.Items)
            {
                if (!zones.ContainsKey(item.Domain))
                    zones[item.Domain] = new AccessibleDnsZone(item.Domain, null, null, item.Owner);
                else if (string.IsNullOrWhiteSpace(zones[item.Domain].Owner))
                    zones[item.Domain] = zones[item.Domain] with { Owner = item.Owner };
            }
            if (accessible.Warning is not null) warnings.Add(accessible.Warning);
        }
        else
        {
            warnings.Add(accessible.Warning ?? "DOMAIN_DISCOVERY_UNAVAILABLE");
        }

        if (zones.Count == 0 && accessible.Items is null)
            return new(adminStatus is 401 or 403 ? adminStatus : accessible.Status, null, "DNS_INVENTORY_UNAVAILABLE");

        return new(200, zones.Values.OrderBy(z => z.Domain, StringComparer.OrdinalIgnoreCase).ToArray(),
            warnings.Count == 0 ? null : string.Join(",", warnings.Distinct()));
    }

    public async Task<AdminDnsZoneListResult> ListAdminZonesAsync(
        AuthenticationHeaderValue auth, int page, CancellationToken ct)
    {
        if (page is < 1 or > 10000) return new(400, null, "INVALID_PAGE");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "CMD_DNS_ADMIN?json=yes&page=" + page.ToString(System.Globalization.CultureInfo.InvariantCulture));
            request.Headers.Authorization = auth;
            using var response = await clients.CreateClient("legacy-directadmin").SendAsync(request,ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized) return new(401,null,"UNAUTHORIZED");
            if (response.StatusCode == HttpStatusCode.Forbidden) return new(403,null,"ACCESS_DENIED");
            if (!response.IsSuccessStatusCode) return new(502,null,"UPSTREAM_ERROR");
            var zones = AdminDnsZonesAdapter.Parse(await response.Content.ReadAsStringAsync(ct));
            return new(200,zones,null);
        }
        catch (LegacyApiException) { return new(403,null,"ACCESS_DENIED"); }
        catch (JsonException) { return new(502,null,"INVALID_LEGACY_RESPONSE"); }
        catch (FormatException) { return new(502,null,"INVALID_LEGACY_RESPONSE"); }
        catch (HttpRequestException) { return new(502,null,"UPSTREAM_UNAVAILABLE"); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        { return new(504,null,"UPSTREAM_TIMEOUT"); }
    }

    /// <summary>DirectAdmin standalone DNS-zone creation/deletion (legacy CMD_API_DNS_ADMIN).</summary>
    public async Task<DnsChangeResult> CreateZoneAsync(DnsZoneCreate input,
        AuthenticationHeaderValue auth, CancellationToken ct)
    {
        if (!LegacyDomainsAdapter.IsValidDomain(input.Domain) ||
            !IPAddress.TryParse(input.Ip, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
            !LegacyDomainsAdapter.IsValidDomain(input.Ns1) ||
            !LegacyDomainsAdapter.IsValidDomain(input.Ns2))
            return new(400, "INVALID_DNS_ZONE");
        return await MutateZoneAsync(new Dictionary<string, string> {
            ["action"] = "create", ["domain"] = input.Domain, ["ip"] = input.Ip,
            ["ns1"] = input.Ns1, ["ns2"] = input.Ns2
        }, auth, ct);
    }

    public async Task<DnsChangeResult> DeleteZoneAsync(string domain, string confirmation,
        AuthenticationHeaderValue auth, CancellationToken ct)
    {
        if (!LegacyDomainsAdapter.IsValidDomain(domain) ||
            !string.Equals(domain, confirmation, StringComparison.Ordinal))
            return new(400, "CONFIRMATION_REQUIRED");
        return await MutateZoneAsync(new Dictionary<string, string> {
            ["action"] = "delete", ["select0"] = domain
        }, auth, ct);
    }

    private async Task<DnsChangeResult> MutateZoneAsync(
        IDictionary<string, string> form, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "CMD_API_DNS_ADMIN");
            request.Headers.Authorization = auth;
            request.Content = new FormUrlEncodedContent(form);
            using var response = await clients.CreateClient("legacy-directadmin")
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                return new((int)response.StatusCode, "UPSTREAM_REJECTED_OPERATION");
            LegacyDomainsAdapter.AssertMutationSucceeded(
                await response.Content.ReadAsStringAsync(ct));
            return new(200, null);
        }
        catch (LegacyApiException) { return new(422, "DIRECTADMIN_REJECTED_OPERATION"); }
        catch (FormatException) { return new(502, "INVALID_LEGACY_RESPONSE"); }
        catch (HttpRequestException) { return new(502, "UPSTREAM_UNAVAILABLE"); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        { return new(504, "UPSTREAM_TIMEOUT"); }
    }

    public async Task<DnsZoneResult> ReadAsync(string domain, AuthenticationHeaderValue auth,
        CancellationToken ct)
    {
        if (!LegacyDomainsAdapter.IsValidDomain(domain)) return new(400, null, "INVALID_DOMAIN");
        try
        {
            // Discover capabilities from DirectAdmin results, never from locally inferred roles.
            // Reads can be retried through the alternative legacy interface safely.
            foreach (var endpoint in new[] { "CMD_DNS_ADMIN", "CMD_DNS_CONTROL" })
            {
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    endpoint + "?domain=" + Uri.EscapeDataString(domain) + "&json=yes&ttl=yes");
                request.Headers.Authorization = auth;
                using var response = await clients.CreateClient("legacy-directadmin").SendAsync(request, ct);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                    return new(401, null, "UNAUTHORIZED");
                if (!response.IsSuccessStatusCode)
                {
                    // CMD_DNS_ADMIN is not a reliable per-domain reader on every
                    // DirectAdmin installation. In particular, it can return HTTP 500
                    // for a zone owned by a regular hosting user. A read-only retry
                    // through CMD_DNS_CONTROL does not duplicate any mutation.
                    if (endpoint == "CMD_DNS_ADMIN") continue;
                    return new((int)response.StatusCode, null,
                        response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound
                            ? "ACCESS_DENIED" : "UPSTREAM_ERROR");
                }
                try
                {
                    var records = LegacyDnsAdapter.ParseRecords(
                        await response.Content.ReadAsStringAsync(ct));
                    return new(200, records, null);
                }
                catch (Exception ex) when (endpoint == "CMD_DNS_ADMIN" &&
                    ex is InvalidOperationException or JsonException or FormatException)
                {
                    // DirectAdmin also reports legacy errors inside HTTP 200 and
                    // occasionally returns a different admin-response shape.
                    // The user-facing DNS endpoint gets a separate read attempt.
                }
            }
            return new(403, null, "ACCESS_DENIED");
        }
        catch (InvalidOperationException) { return new(403, null, "ACCESS_DENIED"); }
        catch (JsonException) { return new(502, null, "INVALID_LEGACY_RESPONSE"); }
        catch (FormatException) { return new(502, null, "INVALID_LEGACY_RESPONSE"); }
        catch (HttpRequestException) { return new(502, null, "UPSTREAM_UNAVAILABLE"); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        { return new(504, null, "UPSTREAM_TIMEOUT"); }
    }

    public async Task<DnsChangeResult> ChangeAsync(string domain, DnsChange change,
        AuthenticationHeaderValue auth, CancellationToken ct)
    {
        if (!LegacyDomainsAdapter.IsValidDomain(domain) || !LegacyDnsAdapter.Valid(change.Record)
            || change.Action is not ("add" or "edit" or "delete")
            || (change.Action != "add" && (change.Previous is null || !LegacyDnsAdapter.Valid(change.Previous))))
            return new(400, "INVALID_DNS_CHANGE");

        try
        {
            var form = LegacyDnsAdapter.Form(domain, change);
            // Only a definitive HTTP denial (403/404) permits a second attempt.
            // Never replay on timeout, HTTP 200 with legacy error, or ambiguous failure.
            foreach (var endpoint in new[] { "CMD_API_DNS_ADMIN", "CMD_API_DNS_CONTROL" })
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                request.Headers.Authorization = auth;
                request.Content = new FormUrlEncodedContent(form);
                using var response = await clients.CreateClient("legacy-directadmin").SendAsync(request, ct);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                    return new(401, "UNAUTHORIZED");
                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                {
                    if (endpoint == "CMD_API_DNS_ADMIN") continue;
                    return new((int)response.StatusCode, "ACCESS_DENIED");
                }
                if (!response.IsSuccessStatusCode)
                    return new((int)response.StatusCode, "UPSTREAM_ERROR");
                var result = await response.Content.ReadAsStringAsync(ct);
                LegacyDomainsAdapter.AssertMutationSucceeded(result);
                return new(200, null);
            }
            return new(403, "ACCESS_DENIED");
        }
        catch (ArgumentException) { return new(400, "INVALID_DNS_CHANGE"); }
        catch (LegacyApiException) { return new(422, "DIRECTADMIN_REJECTED_OPERATION"); }
        catch (FormatException) { return new(502, "INVALID_LEGACY_RESPONSE"); }
        catch (JsonException) { return new(502, "INVALID_LEGACY_RESPONSE"); }
        catch (HttpRequestException) { return new(502, "UPSTREAM_UNAVAILABLE"); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        { return new(504, "UPSTREAM_TIMEOUT"); }
    }
}







