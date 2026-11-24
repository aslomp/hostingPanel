using System.Net.Http.Headers;
using HostingPanel.Api.Adapters;
using System.Net;

namespace HostingPanel.Api.Services;

/// <summary>
/// Legacy DirectAdmin FTP accounts. SFTP uses SSH identities and is deliberately
/// not implemented here: an FTP account does not imply an SSH/SFTP login.
/// Identity, ownership and permitted paths are decided by DirectAdmin.
/// </summary>
public sealed class FileAccessService(IHttpClientFactory clients)
: IFileAccess
{


    public async Task<FileAccessResult> ListFtpAsync(string domain,
        AuthenticationHeaderValue credentials, CancellationToken ct)
    {
        if (!LegacyDomainsAdapter.IsValidDomain(domain))
            return new(400, "INVALID_DOMAIN");
        return await SendAsync(HttpMethod.Get, "CMD_API_FTP?domain=" +
            Uri.EscapeDataString(domain), null, credentials, ct);
    }

    public async Task<FileAccessResult> GetFtpAsync(string domain, string user,
        AuthenticationHeaderValue credentials, CancellationToken ct)
    {
        if (!LegacyDomainsAdapter.IsValidDomain(domain) || !ValidUser(user))
            return new(400, "INVALID_ACCOUNT");
        return await SendAsync(HttpMethod.Get, "CMD_API_FTP_SHOW?domain=" +
            Uri.EscapeDataString(domain) + "&user=" + Uri.EscapeDataString(user),
            null, credentials, ct);
    }

    public Task<FileAccessResult> CreateFtpAsync(FtpAccountRequest input,
        AuthenticationHeaderValue credentials, CancellationToken ct)
        => MutateFtpAsync("create", input, credentials, ct);

    public Task<FileAccessResult> UpdateFtpAsync(string user, FtpAccountRequest input,
        AuthenticationHeaderValue credentials, CancellationToken ct)
        => !string.Equals(user, input.User, StringComparison.Ordinal)
            ? Task.FromResult(new FileAccessResult(400, "USER_MISMATCH"))
            : MutateFtpAsync("modify", input, credentials, ct);

    public async Task<FileAccessResult> DeleteFtpAsync(string domain, string user,
        AuthenticationHeaderValue credentials, CancellationToken ct)
    {
        if (!LegacyDomainsAdapter.IsValidDomain(domain) || !ValidUser(user))
            return new(400, "INVALID_ACCOUNT");
        using var body = new FormUrlEncodedContent(new Dictionary<string,string>
        {
            ["action"] = "delete", ["domain"] = domain, ["select0"] = user
        });
        return await SendAsync(HttpMethod.Post, "CMD_API_FTP", body, credentials, ct);
    }

    private async Task<FileAccessResult> MutateFtpAsync(string action, FtpAccountRequest input,
        AuthenticationHeaderValue credentials, CancellationToken ct)
    {
        if (input is null || !LegacyDomainsAdapter.IsValidDomain(input.Domain)
            || !ValidUser(input.User)
            || input.Type is not ("system" or "domain" or "ftp" or "user" or "custom")
            || string.IsNullOrEmpty(input.Password)
            || (input.Type == "custom" && string.IsNullOrWhiteSpace(input.CustomPath)))
            return new(400, "INVALID_FTP_ACCOUNT");

        var data = new Dictionary<string,string>
        {
            ["action"] = action,
            ["domain"] = input.Domain,
            ["user"] = input.User,
            ["type"] = input.Type,
            ["passwd"] = input.Password,
            ["passwd2"] = input.Password
        };
        if (input.Type == "custom")
            data["custom_val"] = input.CustomPath!;

        using var body = new FormUrlEncodedContent(data);
        return await SendAsync(HttpMethod.Post, "CMD_API_FTP", body, credentials, ct);
    }

    private static bool ValidUser(string? user) =>
        !string.IsNullOrEmpty(user) && user.Length <= 64
        && user.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');

    private async Task<FileAccessResult> SendAsync(HttpMethod method, string path,
        HttpContent? content, AuthenticationHeaderValue credentials, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(method, path) { Content = content };
            request.Headers.Authorization = credentials;
            using var response = await clients.CreateClient("legacy-directadmin")
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            // DirectAdmin may encode operation errors in HTTP 200 bodies.
            // Preserve those bodies verbatim rather than claiming success.
            return new((int)response.StatusCode, await response.Content.ReadAsStringAsync(ct));
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(504, "UPSTREAM_TIMEOUT");
        }
        catch (HttpRequestException)
        {
            return new(502, "UPSTREAM_UNAVAILABLE");
        }
    }
}




