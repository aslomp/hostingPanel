using Microsoft.AspNetCore.Mvc;

namespace HostingPanel.Api.Controllers;

/// <summary>Exact DirectAdmin Swagger routes. Backend determines authorization and support.</summary>
[ApiController]
public sealed class ServerSettingsController(IHostingBackend backend) : BackendController
{
    [HttpPost("/api/server-settings/change-hostname")]
    [HttpGet("/api/server-settings/db-config")]
    [HttpPut("/api/server-settings/db-config")]
    [HttpPost("/api/server-settings/db-config-test")]
    [HttpGet("/api/server-settings/directadmin-conf/active")]
    [HttpGet("/api/server-settings/directadmin-conf/default")]
    [HttpGet("/api/server-settings/directadmin-conf/local")]
    [HttpPut("/api/server-settings/directadmin-conf/local")]
    [HttpPatch("/api/server-settings/directadmin-conf/local")]
    [HttpGet("/api/server-settings/email/config")]
    [HttpPut("/api/server-settings/email/config")]
    [HttpGet("/api/server-settings/email/outbound-filter")]
    [HttpPut("/api/server-settings/email/outbound-filter")]
    [HttpGet("/api/server-settings/timezone/current")]
    [HttpGet("/api/server-settings/timezone/list")]
    [HttpPost("/api/server-settings/timezone/set")]
    public Task ForwardAsync() => ForwardToBackendAsync(HttpContext, backend.Clients);
}
