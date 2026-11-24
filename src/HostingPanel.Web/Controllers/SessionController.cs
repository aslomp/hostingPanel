using Microsoft.AspNetCore.Mvc;

namespace HostingPanel.Api.Controllers;

/// <summary>Exact DirectAdmin Swagger routes. Backend determines authorization and support.</summary>
[ApiController]
public sealed class SessionController(IHostingBackend backend) : BackendController
{
    [HttpGet("/api/session")]
    [HttpPost("/api/session/login-as/return")]
    [HttpPost("/api/session/login-as/switch")]
    [HttpGet("/api/session/login-as/user-list")]
    [HttpGet("/api/session/reseller-config")]
    [HttpGet("/api/session/state")]
    [HttpPost("/api/session/switch-active-domain")]
    [HttpGet("/api/session/user-config")]
    [HttpGet("/api/session/user-usage")]
    [HttpGet("/api/sessions")]
    [HttpPost("/api/sessions/destroy-all-other")]
    [HttpPost("/api/sessions/destroy/{public_id}")]
    public Task ForwardAsync() => backend.ForwardAsync(HttpContext);

    [HttpGet("/api/session/acme-data")]
    public Task AcmeDataAsync() => backend.HandleCertificateRequestAsync(HttpContext);
}
