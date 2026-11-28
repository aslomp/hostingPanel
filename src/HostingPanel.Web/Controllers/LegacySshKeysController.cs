using Microsoft.AspNetCore.Mvc;

namespace HostingPanel.Api.Controllers;

[ApiController]
public sealed class LegacySshKeysController(IApiCompatibilityHandler compatibility, IConfiguration configuration) : BackendController
{
    [HttpGet("/CMD_API_SSH_KEYS")]
    [HttpPost("/CMD_API_SSH_KEYS")]
    public async Task<IResult> Forward(CancellationToken ct)
    {
        if (!TryGetBasic(HttpContext, out _))
            return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);
        if (!string.Equals(configuration["HostingPanel:Backend"] ?? "DirectAdmin", "DirectAdmin", StringComparison.OrdinalIgnoreCase))
            return Results.Json(new { type = "SSH_KEYS_NOT_SUPPORTED" }, statusCode: 501);
        await compatibility.ForwardAsync(HttpContext);
        return Results.Empty;
    }
}
