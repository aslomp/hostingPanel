using Microsoft.AspNetCore.Mvc;

namespace HostingPanel.Api.Controllers;

/// <summary>Exact DirectAdmin Swagger routes. Backend determines authorization and support.</summary>
[ApiController]
public sealed class SystemInfoController(IApiCompatibilityHandler compatibility) : BackendController
{
    [HttpGet("/api/info")]
    [HttpGet("/api/system-info/cpu")]
    [HttpGet("/api/system-info/fs")]
    [HttpGet("/api/system-info/load")]
    [HttpGet("/api/system-info/memory")]
    [HttpGet("/api/system-info/services")]
    [HttpGet("/api/system-info/uptime")]
    public Task ForwardAsync() => compatibility.ForwardAsync(HttpContext);
}
