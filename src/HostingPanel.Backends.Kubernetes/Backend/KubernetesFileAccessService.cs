using System.Net.Http.Headers;
using HostingPanel.Api.Persistence;
using HostingPanel.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace HostingPanel.Api;

public sealed class KubernetesFileAccessService(HostingPanelDbContext db, KubernetesIdentityService identity, ISftpAccounts sftp) : IFileAccess
{
    private async Task<(int Status, string? Owner)> ScopeAsync(string domain, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        if (!KubernetesInputValidation.ValidDomain(domain)) return (400, null);
        var owner = await identity.AuthenticateAsync(auth, ct);
        if (owner is null) return (401, null);
        return await db.Domains.AsNoTracking().AnyAsync(x => x.Name == domain.ToLowerInvariant() && x.OwnerId == owner.Id, ct)
            ? (200, owner.Username) : (404, null);
    }

    private static string Encode(IEnumerable<KeyValuePair<string,string>> fields) =>
        string.Join("&", fields.Select(x => Uri.EscapeDataString(x.Key) + "=" + Uri.EscapeDataString(x.Value)));
    private static FileAccessResult Error(int status, string error) => new(status, Encode([new("error","1"),new("text",error)]));
    private static FileAccessResult Mutation(SftpAccountResult result) => result.Error is null && result.Status is >= 200 and < 300
        ? new(result.Status, Encode([new("error","0"),new("text","Provisioning pending"),new("protocol","sftp")]))
        : Error(result.Status, result.Error ?? "SFTP_OPERATION_FAILED");

    // Domain is an authorization boundary and a view of account-owned SFTP roots.
    private static bool InDomain(SftpAccountEntry row, string domain) =>
        row.RelativePath == "domains/" + domain || row.RelativePath.StartsWith("domains/" + domain + "/", StringComparison.Ordinal);

    public async Task<FileAccessResult> ListFtpAsync(string domain, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var scope = await ScopeAsync(domain, auth, ct);
        if (scope.Status != 200) return Error(scope.Status, "DOMAIN_ACCESS_DENIED");
        var result = await sftp.ListAsync(auth, ct);
        if (result.Status != 200) return Error(result.Status, result.Error ?? "SFTP_LIST_FAILED");
        return new(200, Encode(result.Accounts!.Where(x => InDomain(x, domain.ToLowerInvariant())).Select(x => new KeyValuePair<string,string>("list[]", x.Username))));
    }

    public async Task<FileAccessResult> GetFtpAsync(string domain, string user, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var (status, account) = await FindAsync(domain, user, auth, ct);
        if (account is null) return Error(status, "SFTP_ACCOUNT_UNAVAILABLE");
        return new(200, Encode([new("user",account.Username),new("type","custom"),new("custom_val",account.RelativePath),new("protocol","sftp"),new("status",account.ProvisioningStatus)]));
    }

    private async Task<(int Status, SftpAccountEntry? Account)> FindAsync(string domain, string user, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var scope = await ScopeAsync(domain, auth, ct);
        if (scope.Status != 200) return (scope.Status,null);
        var result = await sftp.ListAsync(auth, ct);
        if (result.Status != 200) return (result.Status,null);
        var account = result.Accounts!.SingleOrDefault(x => x.Username == user && InDomain(x, domain.ToLowerInvariant()));
        return account is null ? (404,null) : (200,account);
    }

    private static string? Root(FtpAccountRequest input)
    {
        var root = "domains/" + input.Domain.ToLowerInvariant();
        return input.Type switch {
            "domain" => root,
            "ftp" => root + "/public_ftp",
            "user" when KubernetesSftpAccounts.ValidRelativePath(input.User) && !input.User.Contains('/') => root + "/public_html/" + input.User,
            "custom" when KubernetesSftpAccounts.ValidRelativePath(input.CustomPath) && !string.IsNullOrEmpty(input.CustomPath) => root + "/" + input.CustomPath,
            _ => null
        };
    }

    public async Task<FileAccessResult> CreateFtpAsync(FtpAccountRequest input, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var scope = await ScopeAsync(input.Domain, auth, ct);
        if (scope.Status != 200) return Error(scope.Status,"DOMAIN_ACCESS_DENIED");
        if (!string.IsNullOrEmpty(input.Password) || input.PublicKeys is not { Count: > 0 }) return Error(400,"SFTP_PUBLIC_KEYS_REQUIRED");
        var root = Root(input);
        if (root is null) return Error(400,"UNSUPPORTED_FTP_ROOT");
        return Mutation(await sftp.CreateAsync(new(input.User,"",root,input.PublicKeys),auth,ct));
    }

    public async Task<FileAccessResult> UpdateFtpAsync(string user, FtpAccountRequest input, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var (status, account) = await FindAsync(input.Domain,user,auth,ct);
        if (account is null) return Error(status,"SFTP_ACCOUNT_UNAVAILABLE");
        if (!string.IsNullOrEmpty(input.Password)) return Error(400,"SFTP_PASSWORD_NOT_SUPPORTED");
        if (input.User != user) return Error(400,"USER_MISMATCH");
        var root = Root(input);
        if (root is null) return Error(400,"UNSUPPORTED_FTP_ROOT");
        return Mutation(await sftp.UpdateAsync(account.Id,new(account.Label,root,account.Blocked,PublicKeys:input.PublicKeys),auth,ct));
    }

    public async Task<FileAccessResult> DeleteFtpAsync(string domain,string user,AuthenticationHeaderValue auth,CancellationToken ct)
    {
        var (status, account) = await FindAsync(domain,user,auth,ct);
        return account is null ? Error(status,"SFTP_ACCOUNT_UNAVAILABLE") : Mutation(await sftp.DeleteAsync(account.Id,auth,ct));
    }
}
