using Microsoft.AspNetCore.Mvc;

namespace HostingPanel.Api.Controllers;

/// <summary>Exact DirectAdmin Swagger routes. Backend determines authorization and support.</summary>
[ApiController]
public sealed class SystemUpdateController(IApiCompatibilityHandler compatibility) : BackendController
{
    [HttpGet("/api/system-packages/history")]
    [HttpGet("/api/system-packages/history/{id}/log")]
    [HttpGet("/api/system-packages/history/{id}/sse")]
    [HttpPost("/api/system-packages/update-run")]
    [HttpPost("/api/system-packages/update-test")]
    [HttpGet("/api/system-packages/updates")]
    public Task ForwardAsync() => compatibility.ForwardAsync(HttpContext);
}
