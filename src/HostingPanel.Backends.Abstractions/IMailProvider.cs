using System.Net.Http.Headers;

namespace HostingPanel.Api;

public sealed record MailAccount(string Name, string Address, long? QuotaBytes, long? UsedBytes, IReadOnlyList<string>? Aliases = null);
public sealed record MailCreateRequest(string Name, string Password, long QuotaBytes);
public sealed record MailUpdateRequest(string? Password, long? QuotaBytes, IReadOnlyList<string>? Aliases = null);
public sealed record MailResult(int Status, string? Error = null, IReadOnlyList<MailAccount>? Accounts = null);

public sealed record MailForwarder(string Name, IReadOnlyList<string> Recipients);
public sealed record MailForwarderResult(int Status, string? Error = null, IReadOnlyList<MailForwarder>? Forwarders = null);

public interface IMailForwarders
{
    Task<MailForwarderResult> ListForwardersAsync(string domain, AuthenticationHeaderValue auth, CancellationToken ct);
    Task<MailForwarderResult> CreateForwarderAsync(string domain, MailForwarder input, AuthenticationHeaderValue auth, CancellationToken ct);
    Task<MailForwarderResult> UpdateForwarderAsync(string domain, MailForwarder input, AuthenticationHeaderValue auth, CancellationToken ct);
    Task<MailForwarderResult> DeleteForwarderAsync(string domain, string name, AuthenticationHeaderValue auth, CancellationToken ct);
}

public interface IMailProvider
{
    Task<MailResult> ListAsync(string domain, AuthenticationHeaderValue auth, CancellationToken ct);
    Task<MailResult> CreateAsync(string domain, MailCreateRequest input, AuthenticationHeaderValue auth, CancellationToken ct);
    Task<MailResult> UpdateAsync(string domain, string name, MailUpdateRequest input, AuthenticationHeaderValue auth, CancellationToken ct);
    Task<MailResult> DeleteAsync(string domain, string name, AuthenticationHeaderValue auth, CancellationToken ct);
}
