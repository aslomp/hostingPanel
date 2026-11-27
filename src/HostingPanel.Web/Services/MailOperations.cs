using HostingPanel.Api;

namespace HostingPanel.Web.Services;

public sealed class MailOperations(BackendOperationScope scope)
{
    private static MailResult Unsupported => new(501, "MAIL_NOT_SUPPORTED");
    public Task<MailResult> ListAsync(string domain, CancellationToken ct = default) =>
        scope.RunOptionalAsync<IMailProvider, MailResult>((p, auth, token) => p.ListAsync(domain, auth, token), Unsupported, ct);
    public Task<MailResult> CreateAsync(string domain, MailCreateRequest input) =>
        scope.RunOptionalAsync<IMailProvider, MailResult>((p, auth, token) => p.CreateAsync(domain, input, auth, token), Unsupported);
    public Task<MailResult> UpdateAsync(string domain, string name, long quotaBytes) =>
        scope.RunOptionalAsync<IMailProvider, MailResult>((p, auth, token) => p.UpdateAsync(domain, name, new(null, quotaBytes), auth, token), Unsupported);
    public Task<MailResult> DeleteAsync(string domain, string name) =>
        scope.RunOptionalAsync<IMailProvider, MailResult>((p, auth, token) => p.DeleteAsync(domain, name, auth, token), Unsupported);
}
