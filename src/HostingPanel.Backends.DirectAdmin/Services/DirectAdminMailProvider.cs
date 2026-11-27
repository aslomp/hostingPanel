using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using HostingPanel.Api.Adapters;
using Microsoft.AspNetCore.WebUtilities;

namespace HostingPanel.Api.Services;

public sealed class DirectAdminMailProvider(IHttpClientFactory clients) : IMailProvider
{
    private static bool ValidName(string? name) => name is { Length: > 0 and <= 64 }
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or '+');
    private static Dictionary<string, string> Fields(string body) =>
        QueryHelpers.ParseQuery(body).ToDictionary(x => x.Key, x => x.Value.Count == 1
            ? x.Value.ToString() : throw new FormatException("Duplicate legacy field."), StringComparer.Ordinal);
    private static long? Bytes(string? value, decimal multiplier) =>
        decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number)
        && number >= 0 && number <= long.MaxValue / multiplier ? (long)(number * multiplier) : null;

    public static IReadOnlyList<MailAccount> ParseInventory(string body, string domain)
    {
        var fields = Fields(body);
        if (fields.TryGetValue("error", out var error) && error != "0")
            throw new LegacyApiException("DirectAdmin rejected mail inventory.");
        var result = new List<MailAccount>();
        foreach (var (name, value) in fields)
        {
            if (name is "error" or "text" or "details") continue;
            if (!ValidName(name)) throw new FormatException("Invalid mailbox name.");
            var detail = Fields(value);
            if (!detail.ContainsKey("quota")) throw new FormatException("Missing mailbox quota.");
            result.Add(new(name, name + "@" + domain,
                Bytes(detail.GetValueOrDefault("quota"), 1048576),
                Bytes(detail.GetValueOrDefault("usage_bytes") ?? detail.GetValueOrDefault("uage_bytes"), 1)
                    ?? Bytes(detail.GetValueOrDefault("usage"), 1048576)));
        }
        return result.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private async Task<(int Status, string Body)> Send(HttpMethod method, string path,
        AuthenticationHeaderValue auth, CancellationToken ct, Dictionary<string,string>? fields = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (fields is not null) request.Content = new FormUrlEncodedContent(fields);
        request.Headers.Authorization = auth;
        using var client = clients.CreateClient("legacy-directadmin");
        using var response = await client.SendAsync(request, ct);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    public async Task<MailResult> ListAsync(string domain, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        if (!LegacyDomainsAdapter.IsValidDomain(domain)) return new(400, "INVALID_DOMAIN");
        try
        {
            var result = await Send(HttpMethod.Get, "CMD_API_POP?action=full_list&domain=" + Uri.EscapeDataString(domain), auth, ct);
            if (result.Status != 200) return new(result.Status, "MAIL_UPSTREAM_ERROR");
            return new(200, Accounts: ParseInventory(result.Body, domain));
        }
        catch (LegacyApiException) { return new(403, "ACCESS_DENIED"); }
        catch (FormatException) { return new(502, "INVALID_LEGACY_RESPONSE"); }
        catch (HttpRequestException) { return new(502, "MAIL_UPSTREAM_ERROR"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(504, "MAIL_UPSTREAM_TIMEOUT"); }
    }

    private async Task<MailResult> Mutate(string domain, string name, string action, Dictionary<string,string> fields,
        AuthenticationHeaderValue auth, CancellationToken ct, long? expectedQuota = null)
    {
        if (!LegacyDomainsAdapter.IsValidDomain(domain) || !ValidName(name)) return new(400, "INVALID_MAIL_ACCOUNT");
        var before = await ListAsync(domain, auth, ct);
        if (before.Accounts is null) return before;
        var exists = before.Accounts.Any(x => x.Name == name);
        if (action == "create" && exists) return new(409, "MAIL_ACCOUNT_EXISTS");
        if (action != "create" && !exists) return new(404, "MAIL_ACCOUNT_NOT_FOUND");
        fields["action"] = action; fields["domain"] = domain; fields["user"] = name;
        try
        {
            var response = await Send(HttpMethod.Post, "CMD_API_POP", auth, ct, fields);
            if (response.Status != 200) return new(response.Status, "MAIL_UPSTREAM_ERROR");
            LegacyDatabaseAdapter.RequireSuccess(response.Body);
            var after = await ListAsync(domain, auth, ct);
            if (after.Accounts is null) return new(502, "CHANGE_UNVERIFIED");
            var account = after.Accounts.SingleOrDefault(x => x.Name == name);
            if (action == "delete" ? account is not null : account is null
                || (expectedQuota.HasValue && account.QuotaBytes != expectedQuota))
                return new(409, "CHANGE_NOT_OBSERVED");
            return new(action == "create" ? 201 : 200);
        }
        catch (LegacyApiException) { return new(422, "DIRECTADMIN_REJECTED_OPERATION"); }
        catch (FormatException) { return new(502, "INVALID_LEGACY_RESPONSE"); }
        catch (JsonException) { return new(502, "INVALID_LEGACY_RESPONSE"); }
        catch (HttpRequestException) { return new(502, "CHANGE_UNVERIFIED"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(504, "CHANGE_UNVERIFIED"); }
    }

    public Task<MailResult> CreateAsync(string domain, MailCreateRequest input, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        if (input.QuotaBytes <= 0 || input.QuotaBytes % 1048576 != 0 || input.Password is not { Length: >= 12 and <= 256 })
            return Task.FromResult(new MailResult(400, "INVALID_MAIL_ACCOUNT"));
        return Mutate(domain, input.Name, "create", new() {
            ["passwd"] = input.Password, ["passwd2"] = input.Password,
            ["quota"] = (input.QuotaBytes / 1048576).ToString(CultureInfo.InvariantCulture)
        }, auth, ct, input.QuotaBytes);
    }

    public Task<MailResult> UpdateAsync(string domain, string name, MailUpdateRequest input, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        if (input.Password is not null) return Task.FromResult(new MailResult(501, "MAIL_PASSWORD_RESET_NOT_SUPPORTED"));
        if (input.QuotaBytes is not long quota || quota <= 0 || quota % 1048576 != 0)
            return Task.FromResult(new MailResult(400, "INVALID_QUOTA"));
        return Mutate(domain, name, "modify", new() { ["quota"] = quota.ToString(CultureInfo.InvariantCulture) }, auth, ct, quota);
    }

    public Task<MailResult> DeleteAsync(string domain, string name, AuthenticationHeaderValue auth, CancellationToken ct) =>
        Mutate(domain, name, "delete", new(), auth, ct);
}
