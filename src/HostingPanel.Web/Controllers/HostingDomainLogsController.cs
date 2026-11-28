using Microsoft.AspNetCore.Mvc;

namespace HostingPanel.Api.Controllers;

[ApiController]
public sealed class HostingDomainLogsController(IEnumerable<IDomainLogs> providers) : BackendController
{
    [HttpGet("/hosting/domains/{domain}/logs/{kind}")]
    public async Task<IResult> Read(string domain,string kind,[FromQuery] long? before,[FromQuery] int count=100,CancellationToken ct=default)
    {
        if (!TryGetBasic(HttpContext,out var auth)) return Results.Unauthorized();
        var provider = providers.SingleOrDefault();
        if (provider is null) return Results.Json(new { type="DOMAIN_LOGS_NOT_SUPPORTED" },statusCode:501);
        var result = await provider.ReadAsync(domain,kind,before,count,auth,ct);
        Response.Headers.CacheControl = "no-store";
        return result.Error is null ? Results.Json(result.Entries,statusCode:result.Status) : Results.Json(new { type=result.Error },statusCode:result.Status);
    }
}
