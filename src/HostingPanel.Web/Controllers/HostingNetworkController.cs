using Microsoft.AspNetCore.Mvc;
namespace HostingPanel.Api.Controllers;
[ApiController]
public sealed class HostingNetworkController(IHostingBackend backend) : BackendController
{
    [HttpGet("/hosting/network/inventory")]
    public async Task Get(CancellationToken ct)
    {
        IResult result = !TryGetBasic(HttpContext, out var auth) ? Results.Unauthorized() :
            backend is IHostingNetworkAccess network ? await network.NetworkInventoryAsync(auth, ct) : Results.StatusCode(501);
        await result.ExecuteAsync(HttpContext);
    }
}
