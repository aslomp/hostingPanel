using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using HostingPanel.Api.Adapters;

namespace HostingPanel.Api.Services;

public sealed class DomainManagementService(IHttpClientFactory clients, IConfiguration configuration)
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
        if (!LegacyDomainsAdapter.IsSafeTestDomain(input.Domain))
            return new(400, "INVALID_TEST_DOMAIN");
        if (input.Action is not ("create" or "modify" or "delete"))
            return new(400, "INVALID_OPERATION");
        if (!string.Equals(input.Confirmation, input.Domain, StringComparison.Ordinal))
            return new(400, "CONFIRMATION_REQUIRED");

        // Deliberately disabled by default even when the caller has admin credentials.
        if (!configuration.GetValue<bool>("HostingPanel:EnableXyz1Mutations"))
            return new(409, "TEST_MUTATIONS_DISABLED");

        var domain = input.Domain.ToLowerInvariant();
        var before = await ListAsync(null, credentials, cancellationToken);
        if (before.Domains is null)
            return new(before.Status, before.Error ?? "PRECONDITION_FAILED");
        var exists = before.Domains.Contains(domain, StringComparer.OrdinalIgnoreCase);
        if (input.Action == "create" && exists)
            return new(409, "DOMAIN_ALREADY_EXISTS");
        if (input.Action is "modify" or "delete" && !exists)
            return new(404, "DOMAIN_NOT_IN_CURRENT_ACCOUNT");

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

    public static bool ValidOwner(string? user) =>
        user is null || (user.Length is > 0 and <= 64 &&
                         user.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'));
}

public sealed record DomainListResult(int Status, IReadOnlyList<string>? Domains, string? Error);

public sealed record DomainCreatePreview(string Domain, bool Php = true, bool Ssl = true, bool Cgi = false);

public sealed record DomainMutationRequest(string Domain, string Action, string Confirmation,
    bool Php = true, bool Ssl = true, bool Cgi = false);
public sealed record DomainMutationResult(int Status, string? Error);
