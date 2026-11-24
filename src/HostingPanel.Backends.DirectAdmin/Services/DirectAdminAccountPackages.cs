using System.Net.Http.Headers;
using System.Text.Json;
using HostingPanel.Api.Adapters;

namespace HostingPanel.Api.Services;

/// <summary>Reads the packages the authenticated DirectAdmin identity may assign.</summary>
public sealed class DirectAdminAccountPackages(IHttpClientFactory clients) : IAccountPackages
{
    public async Task<AccountPackagesResult> ListAsync(string kind, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        if (kind is not ("user" or "reseller")) return new(400, null, "INVALID_ACCOUNT_KIND");
        var path = kind == "user" ? "CMD_API_PACKAGES_USER" : "CMD_API_PACKAGES_RESELLER";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = auth;
            using var response = await clients.CreateClient("legacy-directadmin").SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return new((int)response.StatusCode, null, "PACKAGE_LIST_UNAVAILABLE");
            var body = await response.Content.ReadAsStringAsync(ct);
            return new(200, ParsePackageNames(body), null);
        }
        catch (LegacyApiException) { return new(403, null, "ACCESS_DENIED"); }
        catch (JsonException) { return new(502, null, "INVALID_PACKAGE_LIST"); }
        catch (FormatException) { return new(502, null, "INVALID_PACKAGE_LIST"); }
        catch (HttpRequestException) { return new(502, null, "UPSTREAM_UNAVAILABLE"); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return new(504, null, "UPSTREAM_TIMEOUT"); }
    }

    internal static IReadOnlyList<string> ParsePackageNames(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return [];
        var names = new List<string>();
        if (body.TrimStart().StartsWith('[') || body.TrimStart().StartsWith('{'))
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("error", out var error) && error.ToString() is "1" or "true")
                    throw new LegacyApiException("DirectAdmin rejected package listing.");
                if (root.TryGetProperty("list", out var list)) root = list;
                else if (root.TryGetProperty("packages", out var packages)) root = packages;
                else throw new FormatException("Missing package list.");
            }
            if (root.ValueKind != JsonValueKind.Array) throw new FormatException("Invalid package list.");
            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) throw new FormatException("Invalid package name.");
                names.Add(item.GetString() ?? "");
            }
        }
        else
        {
            foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var pos = pair.IndexOf('=');
                if (pos < 0) throw new FormatException("Invalid package list.");
                var key = System.Net.WebUtility.UrlDecode(pair[..pos]);
                var value = System.Net.WebUtility.UrlDecode(pair[(pos + 1)..]);
                if (key == "error" && value == "1") throw new LegacyApiException("DirectAdmin rejected package listing.");
                if (key == "list[]" || (key.StartsWith("list[", StringComparison.Ordinal) && key.EndsWith(']')) ||
                    key.All(char.IsAsciiDigit)) names.Add(value);
            }
        }
        if (names.Any(n => string.IsNullOrWhiteSpace(n) || n.Length > 128 ||
            n.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))))
            throw new FormatException("Invalid package name.");
        return names.Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
