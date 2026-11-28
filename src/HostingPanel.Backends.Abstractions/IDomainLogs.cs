using System.Net.Http.Headers;

namespace HostingPanel.Api;

public sealed record DomainLogEntry(long Id, string Timestamp, string Message);
public sealed record DomainLogResult(int Status, string? Error = null, IReadOnlyList<DomainLogEntry>? Entries = null);
public interface IDomainLogs
{
    Task<DomainLogResult> ReadAsync(string domain, string kind, long? before, int count, AuthenticationHeaderValue auth, CancellationToken ct);
}
