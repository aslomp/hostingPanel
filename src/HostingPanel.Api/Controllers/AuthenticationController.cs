using Microsoft.AspNetCore.Mvc;

namespace HostingPanel.Api.Controllers;

/// <summary>Exact DirectAdmin Swagger routes. Backend determines authorization and support.</summary>
[ApiController]
public sealed class AuthenticationController(IHostingBackend backend) : BackendController
{
    [HttpPost("/api/change-password")]
    [HttpPost("/api/login")]
    [HttpPost("/api/login/url")]
    [HttpPost("/api/logout")]
    public Task ForwardAsync() => backend.ForwardAsync(HttpContext);
}
