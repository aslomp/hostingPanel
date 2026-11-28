using Microsoft.AspNetCore.Mvc;

namespace HostingPanel.Api.Controllers;

[ApiController]
public sealed class HostingHttpUsageController(IEnumerable<IHttpUsage> providers) : BackendController
{
    [HttpGet("/hosting/users/{username}/usage/http")]
    public async Task<IResult> Read(string username,[FromQuery]string? month,CancellationToken ct)
    {
        if(!TryGetBasic(HttpContext,out var auth)) return Results.Unauthorized();
        var provider=providers.SingleOrDefault();
        if(provider is null) return Results.Json(new { type="HTTP_USAGE_NOT_SUPPORTED" },statusCode:501);
        var result=await provider.ReadAsync(username,month,auth,ct);
        Response.Headers.CacheControl="no-store";
        if(result.Error is not null) return Results.Json(new { type=result.Error },statusCode:result.Status);
        return Results.Ok(new { result.Month,result.ReceivedBytes,result.SentBytes,result.Requests,result.BandwidthLimitBytes,result.LimitKnown,complete=false,source="apache" });
    }
}
