using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using HostingPanel.Api.Adapters;

namespace HostingPanel.Api.Services;

public sealed class LegacyAccountOverviewService(IHttpClientFactory clients)
{
    private static readonly string[] Sensitive =
        ["password", "passwd", "secret", "token", "key", "private", "credential", "hash", "salt", "session", "auth"];

    public async Task<AccountOverviewResult> GetAsync(string username,
        AuthenticationHeaderValue authentication, CancellationToken ct)
    {
        if (!ValidUsername(username)) return new(400, null, null, null, "INVALID_USERNAME");
        var client = clients.CreateClient("legacy-directadmin");

        async Task<(int Status, IReadOnlyDictionary<string,string>? Fields, string? Error)> Fetch(string path)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = authentication;
            using var response = await client.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized) return (401, null, "UNAUTHORIZED");
            if (response.StatusCode == HttpStatusCode.Forbidden) return (403, null, "ACCESS_DENIED");
            if (response.StatusCode == HttpStatusCode.NotFound) return (404, null, "NOT_FOUND");
            if (!response.IsSuccessStatusCode) return (502, null, "UPSTREAM_ERROR");
            var fields = LegacyUserDetailsAdapter.Parse(await response.Content.ReadAsStringAsync(ct));
            return (200, Redact(fields), null);
        }

        try
        {
            var user = await Fetch("CMD_API_SHOW_USER_CONFIG?user=" + Uri.EscapeDataString(username));
            if (user.Fields is null) return new(user.Status, null, null, null, user.Error);
            if (user.Fields.TryGetValue("username", out var reported) &&
                !username.Equals(reported, StringComparison.OrdinalIgnoreCase))
                return new(502, null, null, null, "USERNAME_MISMATCH");

            // Package name comes from the actual user configuration, not from a guessed default.
            if (!user.Fields.TryGetValue("package", out var package) || string.IsNullOrWhiteSpace(package))
                return new(200, user.Fields, null, null, "PACKAGE_NOT_ASSIGNED");

            if (package.Length > 128 || package.Any(char.IsControl) || package.Contains('/') || package.Contains('\\'))
                return new(502, null, null, null, "INVALID_PACKAGE_REFERENCE");

            var settings = await Fetch("CMD_API_PACKAGES_USER?package=" + Uri.EscapeDataString(package));
            // A missing package must not conceal the rest of the user's valid account settings.
            return new(200, user.Fields, package, settings.Fields,
                settings.Fields is null ? settings.Error ?? "PACKAGE_UNAVAILABLE" : null);
        }
        catch (LegacyApiException) { return new(403, null, null, null, "ACCESS_DENIED"); }
        catch (FormatException) { return new(502, null, null, null, "INVALID_LEGACY_RESPONSE"); }
        catch (JsonException) { return new(502, null, null, null, "INVALID_LEGACY_RESPONSE"); }
        catch (HttpRequestException) { return new(502, null, null, null, "UPSTREAM_UNAVAILABLE"); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        { return new(504, null, null, null, "UPSTREAM_TIMEOUT"); }
    }

    public static bool ValidUsername(string username) =>
        !string.IsNullOrWhiteSpace(username) && username.Length <= 64 &&
        username.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    public static IReadOnlyDictionary<string,string> Redact(IReadOnlyDictionary<string,string> fields) =>
        fields.Where(p => !Sensitive.Any(k => p.Key.Contains(k, StringComparison.OrdinalIgnoreCase)))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
}

public sealed record AccountOverviewResult(int Status,
    IReadOnlyDictionary<string,string>? Config, string? Package,
    IReadOnlyDictionary<string,string>? PackageSettings, string? Error);
