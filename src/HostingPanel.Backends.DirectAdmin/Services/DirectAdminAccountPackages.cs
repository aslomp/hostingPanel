using System.Net.Http.Headers;
using System.Text.Json;
using HostingPanel.Api.Adapters;

namespace HostingPanel.Api.Services;

/// <summary>Reads the packages the authenticated DirectAdmin identity may assign.</summary>
public sealed class DirectAdminAccountPackages(IHttpClientFactory clients) : IPackageManagement
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

    public async Task<PackageDetailsResult> GetAsync(string kind, string name, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        if (!PackageFields.ValidKind(kind) || !PackageFields.ValidName(name)) return new(400, null, "INVALID_PACKAGE");
        try
        {
            var path = kind == "user" ? "CMD_API_PACKAGES_USER" : "CMD_API_PACKAGES_RESELLER";
            using var request = new HttpRequestMessage(HttpMethod.Get, path + "?package=" + Uri.EscapeDataString(name));
            request.Headers.Authorization = auth;
            using var response = await clients.CreateClient("legacy-directadmin").SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return new((int)response.StatusCode, null, "PACKAGE_NOT_FOUND");
            var body = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(body)) return new(404, null, "PACKAGE_NOT_FOUND");
            var fields = LegacyUserDetailsAdapter.Parse(body);
            return new(200, new(name, fields), null);
        }
        catch (LegacyApiException) { return new(403, null, "ACCESS_DENIED"); }
        catch (FormatException) { return new(502, null, "INVALID_PACKAGE_RESPONSE"); }
        catch (JsonException) { return new(502, null, "INVALID_PACKAGE_RESPONSE"); }
        catch (HttpRequestException) { return new(502, null, "UPSTREAM_UNAVAILABLE"); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return new(504, null, "UPSTREAM_TIMEOUT"); }
    }

    public async Task<AccountOperationResult> SaveAsync(string kind, HostingPackageDefinition package, bool create, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        if (!PackageFields.ValidKind(kind) || !PackageFields.ValidName(package.Name) || !PackageFields.ValidSettings(kind, package.Settings))
            return new(400, "INVALID_PACKAGE");
        var listed = await ListAsync(kind, auth, ct);
        if (listed.Packages is null) return new(listed.Status, listed.Error);
        var exists = listed.Packages.Contains(package.Name, StringComparer.Ordinal);
        if (create && exists) return new(409, "PACKAGE_EXISTS");
        if (!create && !exists) return new(404, "PACKAGE_NOT_FOUND");
        var values = new Dictionary<string,string>();
        if (!create)
        {
            var current = await GetAsync(kind, package.Name, auth, ct);
            if (current.Package is null) return new(current.Status, current.Error);
            foreach (var field in current.Package.Settings)
                if (PackageFields.ValidSettings(kind, new Dictionary<string,string> { [field.Key] = field.Value })) values[field.Key] = field.Value;
            // Unknown upstream settings may be reset by a full legacy save. Fail closed.
            if (current.Package.Settings.Keys.Any(k => !values.ContainsKey(k) && k is not ("name" or "packagename")))
                return new(409, "PACKAGE_HAS_UNSUPPORTED_SETTINGS");
        }
        foreach (var field in package.Settings) values[field.Key] = field.Value;
        var form = new Dictionary<string,string> { ["add"] = "Save", ["packagename"] = package.Name };
        foreach (var (key, value) in values)
        {
            if (PackageFields.Limits.Contains(key) && value == "unlimited") form["u" + key] = "ON";
            else if (value != "OFF" || key == "dns") form[key] = value;
        }
        var saved = await PostAsync(kind, form, auth, ct);
        if (saved.Error is not null) return saved;
        var observed = await GetAsync(kind, package.Name, auth, ct);
        if (observed.Package is null || values.Any(x => !observed.Package.Settings.TryGetValue(x.Key, out var actual) || !string.Equals(actual, x.Value, StringComparison.OrdinalIgnoreCase)))
            return new(409, "CHANGE_NOT_OBSERVED");
        return new(create ? 201 : 200, null);
    }

    public async Task<AccountOperationResult> DeleteAsync(string kind, string name, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        if (!PackageFields.ValidKind(kind) || !PackageFields.ValidName(name)) return new(400, "INVALID_PACKAGE");
        // DirectAdmin owns package dependencies and mutation authorization.
        var result = await PostAsync(kind, new Dictionary<string,string> { ["delete"] = "Delete", ["delete0"] = name }, auth, ct);
        if (result.Error is not null) return result;
        var listed = await ListAsync(kind, auth, ct);
        return listed.Packages is not null && !listed.Packages.Contains(name, StringComparer.Ordinal)
            ? new(200, null) : new(409, "CHANGE_NOT_OBSERVED");
    }

    private async Task<AccountOperationResult> PostAsync(string kind, Dictionary<string,string> form, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        try
        {
            var path = kind == "user" ? "CMD_API_MANAGE_USER_PACKAGES" : "CMD_API_MANAGE_RESELLER_PACKAGES";
            using var request = new HttpRequestMessage(HttpMethod.Post, path);
            request.Headers.Authorization = auth;
            request.Content = new FormUrlEncodedContent(form);
            using var response = await clients.CreateClient("legacy-directadmin").SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return new((int)response.StatusCode, "UPSTREAM_REJECTED_OPERATION");
            LegacyDomainsAdapter.AssertMutationSucceeded(await response.Content.ReadAsStringAsync(ct));
            return new(200, null);
        }
        catch (LegacyApiException) { return new(422, "DIRECTADMIN_REJECTED_OPERATION"); }
        catch (FormatException) { return new(502, "INVALID_LEGACY_RESPONSE"); }
        catch (HttpRequestException) { return new(502, "UPSTREAM_UNAVAILABLE"); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return new(504, "UPSTREAM_TIMEOUT"); }
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
