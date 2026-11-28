using System.Net.Http.Headers;

namespace HostingPanel.Api;

public sealed record HttpUsageResult(int Status,string? Error=null,string? Month=null,long ReceivedBytes=0,long SentBytes=0,long Requests=0,long? BandwidthLimitBytes=null,bool LimitKnown=false);
public interface IHttpUsage
{
    Task<HttpUsageResult> ReadAsync(string username,string? month,AuthenticationHeaderValue auth,CancellationToken ct);
}
