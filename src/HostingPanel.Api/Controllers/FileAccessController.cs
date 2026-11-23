using HostingPanel.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace HostingPanel.Api.Controllers;

/// <summary>
/// HostingPanel FTP access endpoints, implemented with DirectAdmin's legacy API.
/// These are NOT part of the pinned modern /api/* Swagger contract.
/// SFTP is an SSH capability, not an additional FTP-account type.
/// </summary>
[ApiController]
public sealed class FileAccessController(IHostingBackend backend) : BackendController
{
    [HttpGet("/hosting/file-access/ftp")]
    public async Task<IResult> ListFtp([FromQuery] string domain, CancellationToken ct)
    {
        if (!TryGetBasic(HttpContext, out var auth)) return Unauthorized();
        return Forward(await backend.FileAccess.ListFtpAsync(domain, auth, ct));
    }

    [HttpGet("/hosting/file-access/ftp/{user}")]
    public async Task<IResult> GetFtp([FromRoute] string user, [FromQuery] string domain,
        CancellationToken ct)
    {
        if (!TryGetBasic(HttpContext, out var auth)) return Unauthorized();
        return Forward(await backend.FileAccess.GetFtpAsync(domain, user, auth, ct));
    }

    [HttpPost("/hosting/file-access/ftp")]
    public async Task<IResult> CreateFtp([FromBody] FtpAccountRequest input, CancellationToken ct)
    {
        if (!TryGetBasic(HttpContext, out var auth)) return Unauthorized();
        return Forward(await backend.FileAccess.CreateFtpAsync(input, auth, ct));
    }

    [HttpPut("/hosting/file-access/ftp/{user}")]
    public async Task<IResult> UpdateFtp([FromRoute] string user,
        [FromBody] FtpAccountRequest input, CancellationToken ct)
    {
        if (!TryGetBasic(HttpContext, out var auth)) return Unauthorized();
        return Forward(await backend.FileAccess.UpdateFtpAsync(user, input, auth, ct));
    }

    [HttpDelete("/hosting/file-access/ftp/{user}")]
    public async Task<IResult> DeleteFtp([FromRoute] string user, [FromQuery] string domain,
        CancellationToken ct)
    {
        if (!TryGetBasic(HttpContext, out var auth)) return Unauthorized();
        return Forward(await backend.FileAccess.DeleteFtpAsync(domain, user, auth, ct));
    }

    private static IResult Unauthorized() =>
        Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);

    private static IResult Forward(FileAccessService.UpstreamResult result) =>
        Results.Text(result.Body, "text/plain", statusCode: result.Status);
}
