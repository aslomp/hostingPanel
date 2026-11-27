using Microsoft.AspNetCore.Mvc;

namespace HostingPanel.Api.Controllers;

[ApiController]
public sealed class LegacySubdomainController(IApiCompatibilityHandler compatibility) : BackendController
{
    [HttpGet("/CMD_API_SUBDOMAINS")]
    [HttpPost("/CMD_API_SUBDOMAINS")]
    public Task ForwardAsync() => compatibility.ForwardAsync(HttpContext);
}
