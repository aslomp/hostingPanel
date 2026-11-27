using System.Net.Http.Headers;

namespace HostingPanel.Api;

public sealed record MailAccount(string Name, string Address, long? QuotaBytes, long? UsedBytes);
public sealed record MailCreateRequest(string Name, string Password, long QuotaBytes);
public sealed record MailUpdateRequest(string? Password, long? QuotaBytes);
public sealed record MailResult(int Status, string? Error = null, IReadOnlyList<MailAccount>? Accounts = null);

public interface IMailProvider
{
    Task<MailResult> ListAsync(string domain, AuthenticationHeaderValue auth, CancellationToken ct);
    Task<MailResult> CreateAsync(string domain, MailCreateRequest input, AuthenticationHeaderValue auth, CancellationToken ct);
    Task<MailResult> UpdateAsync(string domain, string name, MailUpdateRequest input, AuthenticationHeaderValue auth, CancellationToken ct);
    Task<MailResult> DeleteAsync(string domain, string name, AuthenticationHeaderValue auth, CancellationToken ct);
}
