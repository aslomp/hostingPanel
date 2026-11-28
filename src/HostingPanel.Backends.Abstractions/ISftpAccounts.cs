using System.Net.Http.Headers;

namespace HostingPanel.Api;

public sealed record SftpAccountEntry(Guid Id, string Username, string Label, string RelativePath = "", string ProvisioningStatus = "pending", bool Blocked = false, IReadOnlyList<string>? PublicKeys = null);
public sealed record SftpAccountRequest(string Label, string PublicKey, string RelativePath = "", IReadOnlyList<string>? PublicKeys = null);
public sealed record SftpAccountUpdate(string Label, string RelativePath, bool Blocked, string? PublicKey = null, IReadOnlyList<string>? PublicKeys = null);
public sealed record SftpAccountResult(int Status, string? Error = null, IReadOnlyList<SftpAccountEntry>? Accounts = null, long Limit = 0, long KeyLimit = 0);
public interface ISftpAccounts
{
    Task<SftpAccountResult> ListAsync(AuthenticationHeaderValue auth, CancellationToken ct);
    Task<SftpAccountResult> CreateAsync(SftpAccountRequest input, AuthenticationHeaderValue auth, CancellationToken ct);
    Task<SftpAccountResult> UpdateAsync(Guid id, SftpAccountUpdate input, AuthenticationHeaderValue auth, CancellationToken ct);
    Task<SftpAccountResult> DeleteAsync(Guid id, AuthenticationHeaderValue auth, CancellationToken ct);
}
