using System.Net.Http.Headers;

namespace HostingPanel.Api;

public sealed record SftpAccountEntry(Guid Id, string Username, string Label, string RelativePath = "", string ProvisioningStatus = "pending");
public sealed record SftpAccountRequest(string Label, string PublicKey, string RelativePath = "");
public sealed record SftpAccountResult(int Status, string? Error = null, IReadOnlyList<SftpAccountEntry>? Accounts = null, long Limit = 0);
public interface ISftpAccounts
{
    Task<SftpAccountResult> ListAsync(AuthenticationHeaderValue auth, CancellationToken ct);
    Task<SftpAccountResult> CreateAsync(SftpAccountRequest input, AuthenticationHeaderValue auth, CancellationToken ct);
    Task<SftpAccountResult> DeleteAsync(Guid id, AuthenticationHeaderValue auth, CancellationToken ct);
}
