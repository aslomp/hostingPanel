using Microsoft.AspNetCore.Mvc;

namespace HostingPanel.Api.Controllers;

[ApiController]
public sealed class BackendSettingsController(IHostingBackend backend) : BackendController
{
    [HttpGet("/hosting/backend-settings")]
    public Task<IResult> Get(CancellationToken ct) =>
        !TryGetBasic(HttpContext, out var auth) ? Task.FromResult(Results.Unauthorized()) :
        backend.Settings?.ReadAsync(auth, ct) ?? Task.FromResult(Results.StatusCode(501));

    [HttpPut("/hosting/backend-settings")]
    public Task<IResult> Put([FromBody] BackendSettingsUpdate update, CancellationToken ct) =>
        !TryGetBasic(HttpContext, out var auth) ? Task.FromResult(Results.Unauthorized()) :
        backend.Settings?.WriteAsync(auth, update, ct) ?? Task.FromResult(Results.StatusCode(501));

    [HttpGet("/hosting/session")]
    public async Task<IResult> Session(CancellationToken ct) =>
        !TryGetBasic(HttpContext, out var auth) ? Results.Unauthorized() :
        Results.StatusCode(await backend.AuthenticateSessionAsync(auth, ct));
}
