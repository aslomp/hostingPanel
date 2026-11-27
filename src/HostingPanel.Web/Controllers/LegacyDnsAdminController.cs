using Microsoft.AspNetCore.Mvc;

namespace HostingPanel.Api.Controllers;

/// <summary>Official legacy DirectAdmin endpoint; backend owns authorization and response semantics.</summary>
[ApiController]
public sealed class LegacyDnsAdminController(IApiCompatibilityHandler compatibility) : BackendController
{
    [HttpGet("/CMD_API_DNS_ADMIN")]
    [HttpPost("/CMD_API_DNS_ADMIN")]
    public Task ForwardAsync() => compatibility.ForwardAsync(HttpContext);
}
