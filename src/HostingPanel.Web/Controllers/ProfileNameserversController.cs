using Microsoft.AspNetCore.Mvc;
namespace HostingPanel.Api.Controllers;

[ApiController]
public sealed class ProfileNameserversController(IAccountNameservers? nameservers = null) : BackendController
{
    [HttpGet("/hosting/profile/nameservers")]
    public async Task<IResult> Get(CancellationToken ct) =>
        !TryGetBasic(HttpContext, out var auth) ? Results.Unauthorized() :
        nameservers is null ? Results.StatusCode(501) : await nameservers.ReadAsync(auth, ct);

    [HttpPut("/hosting/profile/nameservers")]
    public async Task<IResult> Put([FromBody] AccountNameserversUpdate update, CancellationToken ct) =>
        !TryGetBasic(HttpContext, out var auth) ? Results.Unauthorized() :
        nameservers is null ? Results.StatusCode(501) : await nameservers.WriteAsync(auth, update, ct);
}
