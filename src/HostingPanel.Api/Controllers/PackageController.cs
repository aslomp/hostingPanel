using Microsoft.AspNetCore.Mvc;

namespace HostingPanel.Api.Controllers;

/// <summary>Exact DirectAdmin Swagger routes. Backend determines authorization and support.</summary>
[ApiController]
public sealed class PackageController(IHostingBackend backend) : BackendController
{
    [HttpGet("/api/reseller-packages")]
    [HttpGet("/api/reseller-packages/{id}")]
    [HttpDelete("/api/reseller-packages/{id}")]
    public Task ForwardAsync() => ForwardToBackendAsync(HttpContext, backend.Clients);
}
