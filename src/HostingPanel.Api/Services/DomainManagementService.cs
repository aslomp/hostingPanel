using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using HostingPanel.Api.Adapters;

namespace HostingPanel.Api.Services;

public sealed class DomainManagementService(IHttpClientFactory clients)
{
    public async Task<DomainListResult> ListAsync(string? owner,
        AuthenticationHeaderValue credentials, CancellationToken cancellationToken)
    {
        var endpoint = owner is null
            ? "CMD_API_SHOW_DOMAINS"
            : "CMD_API_SHOW_USER_DOMAINS?user=" + Uri.EscapeDataString(owner);

        try
        {
            var client = clients.CreateClient("legacy-directadmin");
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Authorization = credentials;
            using var response = await client.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return new(401, null, "UNAUTHORIZED");
            if (response.StatusCode == HttpStatusCode.Forbidden)
                return new(403, null, "ACCESS_DENIED");
            if (!response.IsSuccessStatusCode)
                return new(502, null, "UPSTREAM_ERROR");
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return new(200, LegacyDomainsAdapter.ParseDomains(body), null);
        }
        catch (LegacyApiException) { return new(403, null, "ACCESS_DENIED"); }
        catch (FormatException) { return new(502, null, "INVALID_LEGACY_RESPONSE"); }
        catch (JsonException) { return new(502, null, "INVALID_LEGACY_RESPONSE"); }
        catch (HttpRequestException) { return new(502, null, "UPSTREAM_UNAVAILABLE"); }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new(504, null, "UPSTREAM_TIMEOUT"); }
    }

    public async Task<DomainMutationResult> MutateAsync(DomainMutationRequest input,
        AuthenticationHeaderValue credentials, CancellationToken cancellationToken)
    {
        if (!LegacyDomainsAdapter.IsValidDomain(input.Domain))
            return new(400, "INVALID_DOMAIN");
        if (input.Action is not ("create" or "modify" or "delete"))
            return new(400, "INVALID_OPERATION");
        if (!string.Equals(input.Confirmation, input.Domain, StringComparison.Ordinal))
            return new(400, "CONFIRMATION_REQUIRED");

        var domain = input.Domain.ToLowerInvariant();
        // Domain ownership and mutation permissions belong to DirectAdmin.
        // Admins and resellers must not be blocked by a local current-account list.
        var form = input.Action switch
        {
            "create" => LegacyDomainsAdapter.EncodeCreateForm(domain, input.Php, input.Ssl, input.Cgi),
            "modify" => LegacyDomainsAdapter.EncodeModifyForm(domain, input.Php, input.Ssl, input.Cgi),
            _ => LegacyDomainsAdapter.EncodeDeleteForm(domain)
        };

        try
        {
            var client = clients.CreateClient("legacy-directadmin");
            using var request = new HttpRequestMessage(HttpMethod.Post, "CMD_API_DOMAIN");
            request.Headers.Authorization = credentials;
            request.Content = new StringContent(form, System.Text.Encoding.UTF8,
                "application/x-www-form-urlencoded");
            using var response = await client.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return new(401, "UNAUTHORIZED");
            if (response.StatusCode == HttpStatusCode.Forbidden)
                return new(403, "ACCESS_DENIED");
            if (!response.IsSuccessStatusCode)
                return new(502, "UPSTREAM_ERROR");
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            LegacyDomainsAdapter.AssertMutationSucceeded(body);
            return new(200, null);
        }
        catch (LegacyApiException) { return new(422, "DIRECTADMIN_REJECTED_OPERATION"); }
        catch (FormatException) { return new(502, "INVALID_LEGACY_RESPONSE"); }
        catch (JsonException) { return new(502, "INVALID_LEGACY_RESPONSE"); }
        catch (HttpRequestException) { return new(502, "UPSTREAM_UNAVAILABLE"); }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new(504, "UPSTREAM_TIMEOUT"); }
    }

    public async Task<DomainDetailResult> GetDetailsAsync(string domain,
        AuthenticationHeaderValue credentials, CancellationToken cancellationToken)
    {
        if (!LegacyDomainsAdapter.IsValidDomain(domain))
            return new(400, null, "INVALID_DOMAIN");

        // Do not require membership in CMD_API_SHOW_DOMAINS (current user's
        // domains). DirectAdmin authorizes admin/reseller access to other accounts.
        try
        {
            var client = clients.CreateClient("legacy-directadmin");
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "CMD_API_ADDITIONAL_DOMAINS?action=view&domain=" + Uri.EscapeDataString(domain));
            request.Headers.Authorization = credentials;
            using var response = await client.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return new(401, null, "UNAUTHORIZED");
            if (response.StatusCode == HttpStatusCode.Forbidden)
                return new(403, null, "ACCESS_DENIED");
            if (!response.IsSuccessStatusCode)
                return new(502, null, "UPSTREAM_ERROR");
            var data = LegacyDomainDetailsAdapter.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken));
            if (!data.TryGetValue("domain", out var reported) ||
                !domain.Equals(reported, StringComparison.OrdinalIgnoreCase))
                return new(502, null, "DOMAIN_MISMATCH");
            return new(200, data, null);
        }
        catch (LegacyApiException) { return new(403, null, "ACCESS_DENIED"); }
        catch (FormatException) { return new(502, null, "INVALID_LEGACY_RESPONSE"); }
        catch (JsonException) { return new(502, null, "INVALID_LEGACY_RESPONSE"); }
        catch (HttpRequestException) { return new(502, null, "UPSTREAM_UNAVAILABLE"); }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new(504, null, "UPSTREAM_TIMEOUT"); }
    }

    public async Task<DomainMutationResult> UpdateSettingAsync(string domain, string setting,
        string value, AuthenticationHeaderValue credentials, CancellationToken cancellationToken)
    {
        if (!LegacyDomainsAdapter.IsValidDomain(domain))
            return new(400, "INVALID_DOMAIN");
        if (!LegacyDomainDetailsAdapter.IsAllowedValue(setting, value))
            return new(400, "INVALID_SETTING");

        // Do not require read permission or a prior settings snapshot for a write.
        // DirectAdmin independently authorizes this exact field update.
        try
        {
            var client = clients.CreateClient("legacy-directadmin");
            using var request = new HttpRequestMessage(HttpMethod.Post, "CMD_API_DOMAIN");
            request.Headers.Authorization = credentials;
            request.Content = new StringContent(
                LegacyDomainDetailsAdapter.EncodeSetting(domain, setting, value),
                System.Text.Encoding.UTF8, "application/x-www-form-urlencoded");
            using var response = await client.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return new(401, "UNAUTHORIZED");
            if (response.StatusCode == HttpStatusCode.Forbidden)
                return new(403, "ACCESS_DENIED");
            if (!response.IsSuccessStatusCode)
                return new(502, "UPSTREAM_ERROR");
            LegacyDomainsAdapter.AssertMutationSucceeded(
                await response.Content.ReadAsStringAsync(cancellationToken));
            return new(200, null);
        }
        catch (LegacyApiException) { return new(422, "DIRECTADMIN_REJECTED_OPERATION"); }
        catch (FormatException) { return new(502, "INVALID_LEGACY_RESPONSE"); }
        catch (JsonException) { return new(502, "INVALID_LEGACY_RESPONSE"); }
        catch (HttpRequestException) { return new(502, "UPSTREAM_UNAVAILABLE"); }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new(504, "UPSTREAM_TIMEOUT"); }
    }

    public static bool ValidOwner(string? user) =>
        user is null || (user.Length is > 0 and <= 64 &&
                         user.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'));
}

public sealed record DomainListResult(int Status, IReadOnlyList<string>? Domains, string? Error);

public sealed record DomainCreatePreview(string Domain, bool Php = true, bool Ssl = true, bool Cgi = false);

public sealed record DomainMutationRequest(string Domain, string Action, string Confirmation,
    bool Php = true, bool Ssl = true, bool Cgi = false);
public sealed record DomainMutationResult(int Status, string? Error);

public sealed record DomainDetailResult(int Status,
    IReadOnlyDictionary<string,string>? Settings, string? Error);
public sealed record DomainSettingRequest(string Setting, string Value);
