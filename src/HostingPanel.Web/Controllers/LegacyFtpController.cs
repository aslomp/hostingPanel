using Microsoft.AspNetCore.Mvc;
namespace HostingPanel.Api.Controllers;
[ApiController]
public sealed class LegacyFtpController(IApiCompatibilityHandler compatibility) : BackendController
{
    [HttpGet("/CMD_API_FTP")]
    [HttpPost("/CMD_API_FTP")]
    [HttpGet("/CMD_API_FTP_SHOW")]
    public Task ForwardAsync() => compatibility.ForwardAsync(HttpContext);
}
