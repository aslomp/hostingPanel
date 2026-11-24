using Microsoft.AspNetCore.Mvc;

namespace HostingPanel.Api.Controllers;

/// <summary>Exact DirectAdmin Swagger routes. Backend determines authorization and support.</summary>
[ApiController]
public sealed class SecurityController(IApiCompatibilityHandler compatibility) : BackendController
{
    [HttpGet("/api/clamav")]
    [HttpPost("/api/clamav")]
    [HttpDelete("/api/clamav/{pid}")]
    [HttpGet("/api/modsecurity-audit-log/entry")]
    [HttpGet("/api/modsecurity-audit-log/summary")]
    [HttpGet("/api/modsecurity/all-configs")]
    [HttpGet("/api/modsecurity/configs/{hostname}")]
    [HttpPut("/api/modsecurity/configs/{hostname}")]
    [HttpDelete("/api/modsecurity/configs/{hostname}")]
    [HttpGet("/api/modsecurity/global-config")]
    [HttpPut("/api/modsecurity/global-config")]
    [HttpGet("/api/modsecurity/user-configs")]
    public Task ForwardAsync() => compatibility.ForwardAsync(HttpContext);
}
