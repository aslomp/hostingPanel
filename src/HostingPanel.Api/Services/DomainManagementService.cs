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

    public static bool ValidOwner(string? user) =>
        user is null || (user.Length is > 0 and <= 64 &&
                         user.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'));
}

public sealed record DomainListResult(int Status, IReadOnlyList<string>? Domains, string? Error);

public sealed record DomainCreatePreview(string Domain, bool Php = true, bool Ssl = true, bool Cgi = false);
