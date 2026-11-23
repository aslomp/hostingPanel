using System.Net;
using System.Net.Http.Headers;
using HostingPanel.Api.Adapters;

namespace HostingPanel.Api.Services;

public sealed record CreateHostingAccount(
    string Kind, string Username, string Email, string Password,
    string Domain, string Package, string Ip = "shared", bool Notify = false);
public sealed record AccountDeletion(string Confirmation);
public sealed record AccountLimitChange(string Field, string Value);
public sealed record AccountSuspension(bool Suspended);
public sealed record AccountOperationResult(int Status, string? Error);

/// <summary>
/// Thin legacy-account command adapter. DirectAdmin owns account hierarchy,
/// scope, resource permissions and command validation.
/// </summary>
public sealed class AccountMutationService(IHttpClientFactory clients)
{
    private static bool ValidUsername(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 64 &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    public static IReadOnlyDictionary<string, string> CreateForm(CreateHostingAccount input)
    {
        if (input.Kind is not ("user" or "reseller") ||
            !ValidUsername(input.Username) ||
            !LegacyDomainsAdapter.IsValidDomain(input.Domain) ||
            input.Password.Length is < 5 or > 256 ||
            string.IsNullOrWhiteSpace(input.Email) || input.Email.Length > 254 ||
            !System.Net.Mail.MailAddress.TryCreate(input.Email, out var mail) ||
            !string.Equals(mail.Address, input.Email, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(input.Package) || input.Package.Length > 128 ||
            input.Package.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')) ||
            string.IsNullOrWhiteSpace(input.Ip) || input.Ip.Length > 128 ||
            input.Ip.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or ':' or '-' or '_')))
            throw new ArgumentException("INVALID_ACCOUNT_REQUEST");

        return new Dictionary<string, string> {
            ["action"] = "create",
            ["add"] = "Submit",
            ["username"] = input.Username,
            ["email"] = input.Email,
            ["passwd"] = input.Password,
            ["passwd2"] = input.Password,
            ["domain"] = input.Domain,
            ["package"] = input.Package,
            ["ip"] = input.Ip,
            ["notify"] = input.Notify ? "yes" : "no"
        };
    }

    public static IReadOnlyDictionary<string, string> DeleteForm(string username, string confirmation)
    {
        if (!ValidUsername(username) || !string.Equals(username, confirmation, StringComparison.Ordinal))
            throw new ArgumentException("CONFIRMATION_REQUIRED");
        return new Dictionary<string, string> {
            ["confirmed"] = "Confirm", ["delete"] = "yes", ["select0"] = username
        };
    }

    public async Task<AccountOperationResult> CreateAsync(
        CreateHostingAccount input, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        IReadOnlyDictionary<string, string> form;
        try { form = CreateForm(input); }
        catch (ArgumentException) { return new(400, "INVALID_ACCOUNT_REQUEST"); }
        return await PostAsync(input.Kind == "reseller" ? "CMD_API_ACCOUNT_RESELLER" :
            "CMD_API_ACCOUNT_USER", form, auth, ct);
    }

    public async Task<AccountOperationResult> DeleteAsync(
        string username, string confirmation, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        IReadOnlyDictionary<string, string> form;
        try { form = DeleteForm(username, confirmation); }
        catch (ArgumentException) { return new(400, "CONFIRMATION_REQUIRED"); }
        return await PostAsync("CMD_API_SELECT_USERS", form, auth, ct);
    }

    /// <summary>
    /// Change a single resource limit without overwriting unrelated account settings.
    /// The backend remains authoritative for authorization and effective values.
    /// </summary>
    public async Task<AccountOperationResult> SetLimitAsync(string kind, string username,
        AccountLimitChange change, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        if (kind is not ("user" or "reseller") || !ValidUsername(username) ||
            change.Field is not ("bandwidth" or "quota" or "inode") ||
            !long.TryParse(change.Value, out var amount) || amount < 0)
            return new(400, "INVALID_LIMIT");
        var endpoint = kind == "reseller" ? "CMD_API_MODIFY_RESELLER" : "CMD_API_MODIFY_USER";
        var form = new Dictionary<string, string> {
            ["action"] = "single", ["user"] = username, [change.Field] = change.Value
        };
        return await PostAndVerifyAsync(endpoint, form, username, change.Field,
            change.Value, auth, ct);
    }

    /// <summary>
    /// Suspend/activate explicitly; no toggle of unknown prior state.
    /// Re-read the backend's authoritative account configuration after POST.
    /// </summary>
    public async Task<AccountOperationResult> SetSuspendedAsync(string username,
        bool suspended, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        if (!ValidUsername(username))
            return new(400, "INVALID_USERNAME");
        var form = new Dictionary<string, string> {
            ["location"] = "CMD_SELECT_USERS",
            ["suspend"] = suspended ? "Suspend" : "Unsuspend",
            ["select0"] = username
        };
        return await PostAndVerifyAsync("CMD_API_SELECT_USERS", form,
            username, "suspended", suspended ? "yes" : "no", auth, ct);
    }

    private async Task<AccountOperationResult> PostAndVerifyAsync(string endpoint,
        IDictionary<string, string> form, string username, string field, string expected,
        AuthenticationHeaderValue auth, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Authorization = auth;
            request.Content = new FormUrlEncodedContent(form);
            using var response = await clients.CreateClient("legacy-directadmin")
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            // DirectAdmin may redirect after a mutation. A redirect is not proof
            // of success, but a subsequent independent GET can confirm the state.
            if (!response.IsSuccessStatusCode &&
                (int)response.StatusCode is not (301 or 302 or 303))
                return new((int)response.StatusCode, "UPSTREAM_REJECTED_OPERATION");

            var body = await response.Content.ReadAsStringAsync(ct);
            if (body.Contains("error=1", StringComparison.OrdinalIgnoreCase))
                return new(422, "DIRECTADMIN_REJECTED_OPERATION");

            using var verify = new HttpRequestMessage(HttpMethod.Get,
                "CMD_API_SHOW_USER_CONFIG?user=" + Uri.EscapeDataString(username));
            verify.Headers.Authorization = auth;
            using var verification = await clients.CreateClient("legacy-directadmin")
                .SendAsync(verify, ct);
            if (!verification.IsSuccessStatusCode)
                return new((int)verification.StatusCode, "CHANGE_UNVERIFIED");

            var current = LegacyUserDetailsAdapter.Parse(
                await verification.Content.ReadAsStringAsync(ct));
            if (!current.TryGetValue(field, out var actual) ||
                !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                return new(409, "CHANGE_NOT_OBSERVED");
            return new(200, null);
        }
        catch (LegacyApiException) { return new(422, "DIRECTADMIN_REJECTED_OPERATION"); }
        catch (FormatException) { return new(502, "INVALID_LEGACY_RESPONSE"); }
        catch (HttpRequestException) { return new(502, "UPSTREAM_UNAVAILABLE"); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        { return new(504, "UPSTREAM_TIMEOUT"); }
    }

    private async Task<AccountOperationResult> PostAsync(string endpoint,
        IReadOnlyDictionary<string, string> values, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Authorization = auth;
            request.Content = new FormUrlEncodedContent(values);
            using var response = await clients.CreateClient("legacy-directadmin")
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                return new((int)response.StatusCode, "UPSTREAM_REJECTED_OPERATION");
            var body = await response.Content.ReadAsStringAsync(ct);
            LegacyDomainsAdapter.AssertMutationSucceeded(body);
            return new((int)response.StatusCode, null);
        }
        catch (LegacyApiException) { return new(422, "DIRECTADMIN_REJECTED_OPERATION"); }
        catch (FormatException) { return new(502, "INVALID_LEGACY_RESPONSE"); }
        catch (HttpRequestException) { return new(502, "UPSTREAM_UNAVAILABLE"); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return new(504, "UPSTREAM_TIMEOUT"); }
    }
}
