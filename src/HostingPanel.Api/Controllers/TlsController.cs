using Microsoft.AspNetCore.Mvc;

namespace HostingPanel.Api.Controllers;

/// <summary>Exact DirectAdmin Swagger routes. Backend determines authorization and support.</summary>
[ApiController]
public sealed class TlsController(IHostingBackend backend) : BackendController
{
    [HttpGet("/api/server-tls/acme-config")]
    [HttpPut("/api/server-tls/acme-config")]
    [HttpGet("/api/server-tls/certificate")]
    [HttpPost("/api/server-tls/enable")]
    [HttpGet("/api/server-tls/files")]
    [HttpPut("/api/server-tls/files")]
    [HttpPost("/api/server-tls/obtain")]
    [HttpGet("/api/server-tls/status")]
    public Task ForwardAsync() => ForwardToBackendAsync(HttpContext, backend.Clients);
}
