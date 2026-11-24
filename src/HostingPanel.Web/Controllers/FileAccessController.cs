using HostingPanel.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace HostingPanel.Api.Controllers;

/// <summary>
/// HostingPanel FTP access endpoints, implemented with DirectAdmin's legacy API.
/// These are NOT part of the pinned modern /api/* Swagger contract.
/// SFTP is an SSH capability, not an additional FTP-account type.
/// </summary>
[ApiController]
public sealed class FileAccessController(IFileAccess fileAccess) : BackendController
{
    [HttpGet("/hosting/file-access/ftp")]
    public async Task<IResult> ListFtp([FromQuery] string domain, CancellationToken ct)
    {
        if (!TryGetBasic(HttpContext, out var auth)) return UnauthorizedResult();
        return Forward(await fileAccess.ListFtpAsync(domain, auth, ct));
    }

    [HttpGet("/hosting/file-access/ftp/{user}")]
    public async Task<IResult> GetFtp([FromRoute] string user, [FromQuery] string domain,
        CancellationToken ct)
    {
        if (!TryGetBasic(HttpContext, out var auth)) return UnauthorizedResult();
        return Forward(await fileAccess.GetFtpAsync(domain, user, auth, ct));
    }

    [HttpPost("/hosting/file-access/ftp")]
    public async Task<IResult> CreateFtp([FromBody] FtpAccountRequest input, CancellationToken ct)
    {
        if (!TryGetBasic(HttpContext, out var auth)) return UnauthorizedResult();
        return Forward(await fileAccess.CreateFtpAsync(input, auth, ct));
    }

    [HttpPut("/hosting/file-access/ftp/{user}")]
    public async Task<IResult> UpdateFtp([FromRoute] string user,
        [FromBody] FtpAccountRequest input, CancellationToken ct)
    {
        if (!TryGetBasic(HttpContext, out var auth)) return UnauthorizedResult();
        return Forward(await fileAccess.UpdateFtpAsync(user, input, auth, ct));
    }

    [HttpDelete("/hosting/file-access/ftp/{user}")]
    public async Task<IResult> DeleteFtp([FromRoute] string user, [FromQuery] string domain,
        CancellationToken ct)
    {
        if (!TryGetBasic(HttpContext, out var auth)) return UnauthorizedResult();
        return Forward(await fileAccess.DeleteFtpAsync(domain, user, auth, ct));
    }

    private static IResult UnauthorizedResult() =>
        Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);

    private static IResult Forward(FileAccessResult result) =>
        Results.Text(result.Body, "text/plain", statusCode: result.Status);
}
