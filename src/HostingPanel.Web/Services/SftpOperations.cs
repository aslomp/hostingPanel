using HostingPanel.Api;
namespace HostingPanel.Web.Services;
public sealed class SftpOperations(BackendOperationScope scope)
{
    private static SftpAccountResult Unsupported => new(501, "SFTP_NOT_SUPPORTED");
    public Task<SftpAccountResult> ListAsync(CancellationToken ct = default) =>
        scope.RunOptionalAsync<ISftpAccounts, SftpAccountResult>((p, auth, token) => p.ListAsync(auth, token), Unsupported, ct);
    public Task<SftpAccountResult> CreateAsync(SftpAccountRequest input) =>
        scope.RunOptionalAsync<ISftpAccounts, SftpAccountResult>((p, auth, token) => p.CreateAsync(input, auth, token), Unsupported);
    public Task<SftpAccountResult> DeleteAsync(Guid id) =>
        scope.RunOptionalAsync<ISftpAccounts, SftpAccountResult>((p, auth, token) => p.DeleteAsync(id, auth, token), Unsupported);
}
