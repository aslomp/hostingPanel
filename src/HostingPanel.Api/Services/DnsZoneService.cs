using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using HostingPanel.Api.Adapters;

namespace HostingPanel.Api.Services;

public sealed class DnsZoneService(IHttpClientFactory clients)
{
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
                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                {
                    if (endpoint == "CMD_DNS_ADMIN") continue;
                    return new((int)response.StatusCode, null, "ACCESS_DENIED");
                }
                if (!response.IsSuccessStatusCode)
                    return new((int)response.StatusCode, null, "UPSTREAM_ERROR");
                try
                {
                    var records = LegacyDnsAdapter.ParseRecords(
                        await response.Content.ReadAsStringAsync(ct));
                    return new(200, records, null);
                }
                catch (InvalidOperationException) when (endpoint == "CMD_DNS_ADMIN")
                {
                    // Legacy backends sometimes report denial inside HTTP 200.
                    // A second READ is safe; this is not an authorization decision.
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

public sealed record DnsZoneResult(int Status, IReadOnlyList<DnsRecord>? Records, string? Error);
public sealed record DnsChangeResult(int Status, string? Error);

public sealed record AdminDnsZoneListResult(int Status, IReadOnlyList<AdminDnsZone>? Zones, string? Error);
