using System.Net;
using System.Net.Http.Headers;
using HostingPanel.Api.Adapters;

namespace HostingPanel.Api.Services;

public sealed record CreateHostingAccount(
    string Kind, string Username, string Email, string Password,
    string Domain, string Package, string Ip = "shared", bool Notify = false);
public sealed record AccountDeletion(string Confirmation);
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
