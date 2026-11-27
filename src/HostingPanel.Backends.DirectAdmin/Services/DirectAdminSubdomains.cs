using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using HostingPanel.Api.Adapters;

namespace HostingPanel.Api.Services;

public sealed class DirectAdminSubdomains(IHttpClientFactory clients) : ISubdomains
{
    public async Task<SubdomainInventory> ListAsync(string domain, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        if (!SubdomainNames.ValidDomain(domain)) return new(400, null, "INVALID_DOMAIN");
        try
        {
            using var client = clients.CreateClient("legacy-directadmin");
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "CMD_API_SUBDOMAINS?domain=" + Uri.EscapeDataString(domain.ToLowerInvariant()));
            request.Headers.Authorization = auth;
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return new(MapStatus(response.StatusCode), null, "UPSTREAM_ERROR");
            var names = LegacyDomainsAdapter.ParseDomains(await response.Content.ReadAsStringAsync(ct));
            var suffix = "." + domain;
            var labels = names.Select(x => x.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? x[..^suffix.Length] : x)
                .Select(x => x.ToLowerInvariant()).ToArray();
            if (labels.Any(x => !SubdomainNames.ValidLabel(x))) return new(502, null, "INVALID_LEGACY_RESPONSE");
            return new(200, labels.Distinct(StringComparer.Ordinal).Order().ToArray(), null);
        }
        catch (LegacyApiException) { return new(403, null, "ACCESS_DENIED"); }
        catch (Exception ex) when (ex is FormatException or JsonException) { return new(502, null, "INVALID_LEGACY_RESPONSE"); }
        catch (HttpRequestException) { return new(502, null, "UPSTREAM_UNAVAILABLE"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(504, null, "UPSTREAM_TIMEOUT"); }
    }

    public Task<SubdomainMutation> CreateAsync(string domain, string label, AuthenticationHeaderValue auth, CancellationToken ct)
        => MutateAsync(domain, label, null, false, auth, ct);

    public Task<SubdomainMutation> DeleteAsync(string domain, string label, string confirmation, AuthenticationHeaderValue auth, CancellationToken ct)
        => MutateAsync(domain, label, confirmation, true, auth, ct);

    private async Task<SubdomainMutation> MutateAsync(string domain, string label, string? confirmation, bool delete,
        AuthenticationHeaderValue auth, CancellationToken ct)
    {
        if (!SubdomainNames.ValidDomain(domain) || !SubdomainNames.ValidLabel(label) ||
            !SubdomainNames.ValidDomain(label + "." + domain)) return new(400, "INVALID_SUBDOMAIN");
        domain = domain.ToLowerInvariant();
        label = label.ToLowerInvariant();
        if (delete && confirmation != label + "." + domain) return new(400, "CONFIRMATION_REQUIRED");
        try
        {
            using var client = clients.CreateClient("legacy-directadmin");
            using var request = new HttpRequestMessage(HttpMethod.Post, "CMD_API_SUBDOMAINS");
            request.Headers.Authorization = auth;
            var fields = new Dictionary<string, string> { ["domain"] = domain, ["action"] = delete ? "delete" : "create" };
            if (delete) { fields["select0"] = label; fields["contents"] = "yes"; }
            else fields["subdomain"] = label;
            request.Content = new FormUrlEncodedContent(fields);
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return new(MapStatus(response.StatusCode), "UPSTREAM_ERROR");
            LegacyDomainsAdapter.AssertMutationSucceeded(await response.Content.ReadAsStringAsync(ct));
            var observed = await ListAsync(domain, auth, ct);
            if (observed.Status != 200 || observed.Names is null || observed.Names.Contains(label) == delete)
                return new(502, "SUBDOMAIN_MUTATION_UNVERIFIED");
            return new(200, null);
        }
        catch (LegacyApiException) { return new(422, "DIRECTADMIN_REJECTED_OPERATION"); }
        catch (Exception ex) when (ex is FormatException or JsonException) { return new(502, "INVALID_LEGACY_RESPONSE"); }
        catch (HttpRequestException) { return new(502, "UPSTREAM_UNAVAILABLE"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(504, "UPSTREAM_TIMEOUT"); }
    }

    private static int MapStatus(HttpStatusCode code) => code is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound
        ? (int)code : 502;
}
