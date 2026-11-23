using Microsoft.AspNetCore.Mvc;

namespace HostingPanel.Api.Controllers;

/// <summary>Exact DirectAdmin Swagger routes. Backend determines authorization and support.</summary>
[ApiController]
public sealed class ResellerController(IHostingBackend backend) : BackendController
{
    [HttpGet("/api/resellers/{username}/config")]
    [HttpGet("/api/resellers/{username}/usage")]
    public Task ForwardAsync() => ForwardToBackendAsync(HttpContext, backend.Clients);
}
