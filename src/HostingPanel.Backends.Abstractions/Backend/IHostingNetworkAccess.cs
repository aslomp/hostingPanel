using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
namespace HostingPanel.Api;
public sealed record NetworkDeviceAddress(string Address, string Device, int PrefixLength);
public interface IHostingNetworkAccess
{
    Task<IResult> NetworkInventoryAsync(AuthenticationHeaderValue auth, CancellationToken ct);
}
