using Microsoft.AspNetCore.Mvc;

namespace HostingPanel.Api.Controllers;

[ApiController]
[Route("hosting/websites")]
public sealed class WebHostingController(IWebHosting webHosting) : BackendController
{
    [HttpGet]
    public async Task<IResult> List([FromQuery] string? owner, CancellationToken ct)
    {
        if (!TryGetBasic(HttpContext, out var auth)) return Results.Unauthorized();
        var result = await webHosting.ListWebsitesAsync(owner, auth, ct);
        return Results.Json(result, statusCode: result.Status);
    }

    [HttpGet("{domain}")]
    public async Task<IResult> Get(string domain, CancellationToken ct)
    {
        if (!TryGetBasic(HttpContext, out var auth)) return Results.Unauthorized();
        var result = await webHosting.GetWebsiteAsync(domain, auth, ct);
        return Results.Json(result, statusCode: result.Status);
    }

    [HttpPost]
    public async Task<IResult> Create([FromBody] WebsiteConfiguration website, CancellationToken ct)
    {
        if (!TryGetBasic(HttpContext, out var auth)) return Results.Unauthorized();
        var result = await webHosting.CreateWebsiteAsync(website, auth, ct);
        return Results.Json(result, statusCode: result.Status);
    }

    [HttpPut("{domain}")]
    public async Task<IResult> Update(string domain, [FromBody] WebsiteConfiguration website, CancellationToken ct)
    {
        if (!TryGetBasic(HttpContext, out var auth)) return Results.Unauthorized();
        if (!string.Equals(domain, website.Domain, StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { type = "DOMAIN_MISMATCH" });
        var result = await webHosting.UpdateWebsiteAsync(website, auth, ct);
        return Results.Json(result, statusCode: result.Status);
    }

    [HttpDelete("{domain}")]
    public async Task<IResult> Delete(string domain, [FromBody] WebsiteDeletion input, CancellationToken ct)
    {
        if (!TryGetBasic(HttpContext, out var auth)) return Results.Unauthorized();
        var result = await webHosting.DeleteWebsiteAsync(domain, input.Confirmation, auth, ct);
        return Results.Json(result, statusCode: result.Status);
    }
}

public sealed record WebsiteDeletion(string Confirmation);
