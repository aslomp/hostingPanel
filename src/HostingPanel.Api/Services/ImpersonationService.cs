using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HostingPanel.Api.Adapters;

namespace HostingPanel.Api.Services;

/// <summary>
/// Authorizes a DirectAdmin login-as transition without modifying the caller's
/// credentials, creating a second password, or storing privileged sessions.
/// DirectAdmin remains the authority for delegated access.
/// </summary>
public sealed class ImpersonationService(IHttpClientFactory clients)
{
    public async Task<ImpersonationCheck> CheckAsync(
        AuthenticationHeaderValue authorization, string target, CancellationToken ct)
    {
        if (!LegacyAccountOverviewService.ValidUsername(target))
            return new(400, "INVALID_TARGET");
        if (!TryDecode(authorization, out var admin, out var key))
            return new(401, "INVALID_CREDENTIALS");
        if (!LegacyAccountOverviewService.ValidUsername(admin) || admin.Equals(target,
            StringComparison.OrdinalIgnoreCase))
            return new(400, "INVALID_TARGET");

        try
        {
            var client = clients.CreateClient("legacy-directadmin");

            // Only an authenticated administrator may enter another user's context.
            // Access to a user-list on its own does NOT prove admin privilege.
            using var own = new HttpRequestMessage(HttpMethod.Get,
                "CMD_API_SHOW_USER_CONFIG?user=" + Uri.EscapeDataString(admin));
            own.Headers.Authorization = Authorization(admin, key);
            using var ownResponse = await client.SendAsync(own, ct);
            if (ownResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new(403, "ADMIN_ACCESS_REQUIRED");
            if (!ownResponse.IsSuccessStatusCode)
                return new(502, "UPSTREAM_ERROR");
            var config = LegacyUserDetailsAdapter.Parse(
                await ownResponse.Content.ReadAsStringAsync(ct));
            if (!config.TryGetValue("usertype", out var role) ||
                !role.Equals("admin", StringComparison.OrdinalIgnoreCase))
                return new(403, "ADMIN_ACCESS_REQUIRED");
            if (config.TryGetValue("username", out var reported) &&
                !reported.Equals(admin, StringComparison.OrdinalIgnoreCase))
                return new(403, "ADMIN_ACCESS_REQUIRED");

            // Have DirectAdmin authenticate the delegated identity *before* the
            // Blazor circuit changes identity. No local permission override exists.
            var delegatedUsername = admin + "|" + target;
            using var probe = new HttpRequestMessage(HttpMethod.Get, "api/session");
            probe.Headers.Authorization = Authorization(delegatedUsername, key);
            using var probeResponse = await client.SendAsync(probe, ct);
            if (probeResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new(403, "IMPERSONATION_DENIED");
            if (probeResponse.StatusCode == HttpStatusCode.NotFound)
                return new(501, "UPSTREAM_SESSION_API_UNAVAILABLE");
            if (!probeResponse.IsSuccessStatusCode)
                return new(502, "UPSTREAM_ERROR");

            using var document = JsonDocument.Parse(
                await probeResponse.Content.ReadAsStringAsync(ct));
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("realUsername", out var real) ||
                !document.RootElement.TryGetProperty("effectiveUsername", out var effective) ||
                real.ValueKind != JsonValueKind.String ||
                effective.ValueKind != JsonValueKind.String ||
                !admin.Equals(real.GetString(), StringComparison.OrdinalIgnoreCase) ||
                !target.Equals(effective.GetString(), StringComparison.OrdinalIgnoreCase))
                return new(403, "IDENTITY_NOT_VERIFIED");

            return new(200, null);
        }
        catch (LegacyApiException) { return new(403, "ADMIN_ACCESS_REQUIRED"); }
        catch (JsonException) { return new(502, "INVALID_UPSTREAM_RESPONSE"); }
        catch (FormatException) { return new(502, "INVALID_UPSTREAM_RESPONSE"); }
        catch (HttpRequestException) { return new(502, "UPSTREAM_UNAVAILABLE"); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        { return new(504, "UPSTREAM_TIMEOUT"); }
    }

    public static bool TryDecode(AuthenticationHeaderValue header, out string username, out string secret)
    {
        username = ""; secret = "";
        if (!header.Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(header.Parameter)) return false;
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header.Parameter));
            var separator = decoded.IndexOf(':');
            if (separator <= 0 || separator == decoded.Length - 1) return false;
            username = decoded[..separator];
            secret = decoded[(separator + 1)..];
            return !username.Contains('|') && !username.Any(char.IsControl);
        }
        catch (FormatException) { return false; }
    }

    private static AuthenticationHeaderValue Authorization(string username, string key) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + key)));
}
public sealed record ImpersonationCheck(int Status, string? Error);
