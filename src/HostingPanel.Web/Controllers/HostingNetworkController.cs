using Microsoft.AspNetCore.Mvc;
namespace HostingPanel.Api.Controllers;
[ApiController]
public sealed class HostingNetworkController(IHostingNetworkAccess? network = null) : BackendController
{
    [HttpGet("/hosting/network/inventory")]
    public async Task Get(CancellationToken ct)
    {
        IResult result = !TryGetBasic(HttpContext, out var auth) ? Results.Unauthorized() :
            network is not null ? await network.NetworkInventoryAsync(auth, ct) : Results.StatusCode(501);
        await result.ExecuteAsync(HttpContext);
    }
}
