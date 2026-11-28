using HostingPanel.Api;
using HostingPanel.Api.Services;
namespace HostingPanel.Web.Services;
public sealed class SftpOperations(BackendOperationScope scope)
{
    public Task<FileAccessResult> ListFtpAsync(string domain) => scope.RunAsync<IFileAccess,FileAccessResult>((p,a,ct) => p.ListFtpAsync(domain,a,ct));
    public Task<FileAccessResult> CreateFtpAsync(FtpAccountRequest input) => scope.RunAsync<IFileAccess,FileAccessResult>((p,a,ct) => p.CreateFtpAsync(input,a,ct));
    public Task<FileAccessResult> UpdateFtpAsync(string user,FtpAccountRequest input) => scope.RunAsync<IFileAccess,FileAccessResult>((p,a,ct) => p.UpdateFtpAsync(user,input,a,ct));
    public Task<FileAccessResult> DeleteFtpAsync(string domain,string user) => scope.RunAsync<IFileAccess,FileAccessResult>((p,a,ct) => p.DeleteFtpAsync(domain,user,a,ct));
    private static SftpAccountResult Unsupported => new(501, "SFTP_NOT_SUPPORTED");
    public Task<SftpAccountResult> ListAsync(CancellationToken ct = default) =>
        scope.RunOptionalAsync<ISftpAccounts, SftpAccountResult>((p, auth, token) => p.ListAsync(auth, token), Unsupported, ct);
    public Task<SftpAccountResult> CreateAsync(SftpAccountRequest input) =>
        scope.RunOptionalAsync<ISftpAccounts, SftpAccountResult>((p, auth, token) => p.CreateAsync(input, auth, token), Unsupported);
    public Task<SftpAccountResult> UpdateAsync(Guid id, SftpAccountUpdate input) =>
        scope.RunOptionalAsync<ISftpAccounts, SftpAccountResult>((p, auth, token) => p.UpdateAsync(id, input, auth, token), Unsupported);
    public Task<SftpAccountResult> DeleteAsync(Guid id) =>
        scope.RunOptionalAsync<ISftpAccounts, SftpAccountResult>((p, auth, token) => p.DeleteAsync(id, auth, token), Unsupported);
}
