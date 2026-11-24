using Microsoft.AspNetCore.Mvc;

namespace HostingPanel.Api.Controllers;

[ApiController]
public sealed class BackendSettingsController(IHostingBackend backend) : BackendController
{
    [HttpGet("/hosting/backend-settings")]
    public async Task Get(CancellationToken ct)
    {
        var result = !TryGetBasic(HttpContext, out var auth) ? Results.Unauthorized() :
            backend.Settings is { } settings ? await settings.ReadAsync(auth, ct) : Results.StatusCode(501);
        await result.ExecuteAsync(HttpContext);
    }

    [HttpPut("/hosting/backend-settings")]
    public async Task Put([FromBody] BackendSettingsUpdate update, CancellationToken ct)
    {
        var result = !TryGetBasic(HttpContext, out var auth) ? Results.Unauthorized() :
            backend.Settings is { } settings ? await settings.WriteAsync(auth, update, ct) : Results.StatusCode(501);
        await result.ExecuteAsync(HttpContext);
    }

    [HttpGet("/hosting/session")]
    public async Task<IResult> Session(CancellationToken ct) =>
        !TryGetBasic(HttpContext, out var auth) ? Results.Unauthorized() :
        Results.StatusCode(await backend.AuthenticateSessionAsync(auth, ct));
}
