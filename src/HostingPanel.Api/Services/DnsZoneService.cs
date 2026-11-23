using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using HostingPanel.Api.Adapters;

namespace HostingPanel.Api.Services;

public sealed class DnsZoneService(IHttpClientFactory clients)
{
    /// <summary>Determine whether DirectAdmin authenticated this request as a real admin.
    /// A delegated admin|user login is not treated as an administrator.</summary>
    private async Task<bool> IsAdminAsync(AuthenticationHeaderValue auth, CancellationToken ct)
    {
        if (!ImpersonationService.TryDecode(auth, out var username, out _)) return false;
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "CMD_API_SHOW_USER_CONFIG?user=" + Uri.EscapeDataString(username));
        request.Headers.Authorization = auth;
        using var response = await clients.CreateClient("legacy-directadmin").SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return false;
        var config = LegacyUserDetailsAdapter.Parse(await response.Content.ReadAsStringAsync(ct));
        return config.TryGetValue("usertype", out var role) &&
            role.Equals("admin", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<DnsZoneResult> ReadAsync(string domain, AuthenticationHeaderValue auth,
        CancellationToken ct)
    {
        if (!LegacyDomainsAdapter.IsValidDomain(domain)) return new(400, null, "INVALID_DOMAIN");
        try
        {
            var administrator = await IsAdminAsync(auth, ct);
            using var request = new HttpRequestMessage(HttpMethod.Get,
                (administrator ? "CMD_DNS_ADMIN" : "CMD_DNS_CONTROL") +
                "?domain=" + Uri.EscapeDataString(domain) + "&json=yes&ttl=yes");
            request.Headers.Authorization = auth;
            using var response = await clients.CreateClient("legacy-directadmin").SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized) return new(401, null, "UNAUTHORIZED");
            if (response.StatusCode == HttpStatusCode.Forbidden) return new(403, null, "ACCESS_DENIED");
            if (response.StatusCode == HttpStatusCode.NotFound) return new(404, null, "ZONE_NOT_FOUND");
            if (!response.IsSuccessStatusCode) return new(502, null, "UPSTREAM_ERROR");
            var records = LegacyDnsAdapter.ParseRecords(await response.Content.ReadAsStringAsync(ct));
            return new(200, records, null);
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

        var snapshot = await ReadAsync(domain, auth, ct);
        if (snapshot.Records is null) return new(snapshot.Status, snapshot.Error);

        var exists = snapshot.Records.Any(r => LegacyDnsAdapter.Identical(r,
            change.Action == "add" ? change.Record : change.Previous!));
        if (change.Action != "add" && !exists) return new(409, "RECORD_HAS_CHANGED");
        if (change.Action == "add" && exists) return new(409, "RECORD_ALREADY_EXISTS");
        try
        {
            var form = LegacyDnsAdapter.Form(domain, change);
            var administrator = await IsAdminAsync(auth, ct);
            using var request = new HttpRequestMessage(HttpMethod.Post,
                administrator ? "CMD_API_DNS_ADMIN" : "CMD_API_DNS_CONTROL");
            request.Headers.Authorization = auth;
            request.Content = new FormUrlEncodedContent(form);
            using var response = await clients.CreateClient("legacy-directadmin").SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized) return new(401, "UNAUTHORIZED");
            if (response.StatusCode == HttpStatusCode.Forbidden) return new(403, "ACCESS_DENIED");
            if (!response.IsSuccessStatusCode) return new(502, "UPSTREAM_ERROR");
            var result = await response.Content.ReadAsStringAsync(ct);
            LegacyDomainsAdapter.AssertMutationSucceeded(result);
            // "error=0" from old DirectAdmin versions doesn't always mean a record changed.
            // Read the zone again and require the intended exact record delta.
            var after = await ReadAsync(domain, auth, ct);
            if (after.Records is null)
                return new(502, "CHANGE_UNVERIFIED");
            return LegacyDnsAdapter.Applied(snapshot.Records, after.Records, change)
                ? new(200, null) : new(409, "CHANGE_NOT_OBSERVED");
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
