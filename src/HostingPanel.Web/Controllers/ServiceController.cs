using Microsoft.AspNetCore.Mvc;

namespace HostingPanel.Api.Controllers;

/// <summary>Exact DirectAdmin Swagger routes. Backend determines authorization and support.</summary>
[ApiController]
public sealed class ServiceController(IHostingBackend backend) : BackendController
{
    [HttpPost("/api/system-services-actions/service/{service}/reload")]
    [HttpPost("/api/system-services-actions/service/{service}/restart")]
    [HttpPost("/api/system-services-actions/service/{service}/start")]
    [HttpPost("/api/system-services-actions/service/{service}/stop")]
    [HttpPut("/api/system-services-actions/service/{service}/watchdog")]
    [HttpGet("/api/system-services/list")]
    [HttpGet("/api/system-services/service/{service}")]
    [HttpGet("/api/system-services/service/{service}/log")]
    public Task ForwardAsync() => backend.ForwardAsync(HttpContext);
}
