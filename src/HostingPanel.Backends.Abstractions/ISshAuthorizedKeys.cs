using System.Net.Http.Headers;

namespace HostingPanel.Api;

public sealed record SshAuthorizedKey(string Fingerprint, string PublicKey);
public sealed record SshKeyResult(int Status, string? Error = null, IReadOnlyList<SshAuthorizedKey>? Keys = null);
public interface ISshAuthorizedKeys
{
    Task<SshKeyResult> ListAsync(AuthenticationHeaderValue auth, CancellationToken ct);
    Task<SshKeyResult> AuthorizeAsync(string key, AuthenticationHeaderValue auth, CancellationToken ct);
    Task<SshKeyResult> RevokeAsync(string fingerprint, AuthenticationHeaderValue auth, CancellationToken ct);
}
