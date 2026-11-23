using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using HostingPanel.Api.Adapters;
using HostingPanel.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace HostingPanel.Api.Controllers;

[ApiController]
public sealed class DnsController(IHostingBackend backend) : BackendController
{
    /// <summary>
    /// Overall DNS zone list sourced from the backend-accessible domain catalog.
    /// No local admin/reseller/user role restriction.
    /// </summary>
    [HttpGet("/hosting/dns/zones/accessible")]
    public async Task<IResult> ListAccessibleDnsZones(CancellationToken ct)
    {
        if (!TryGetBasic(HttpContext, out var auth))
            return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);

        var result = await backend.AccessibleDomains.ListAsync(auth, ct);
        return result.Items is null
            ? Results.Json(new { type = result.Warning ?? "UPSTREAM_ERROR" }, statusCode: result.Status)
            : Results.Json(new { zones = result.Items, warning = result.Warning });
    }

    [HttpGet("/hosting/dns/zones")]
    public async Task<IResult> hosting_dns_zones_Get_19()
    {
        var context = HttpContext;
        var service = backend.Dns;
        if (!TryGetBasic(context, out var auth))
                return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);
            var rawPage = context.Request.Query["page"].ToString();
            if (!int.TryParse(string.IsNullOrEmpty(rawPage) ? "1" : rawPage, out var page)
                || page is < 1 or > 10000)
                return Results.Json(new { type = "INVALID_PAGE" }, statusCode: 400);
            var result = await service.ListAdminZonesAsync(auth, page, context.RequestAborted);
            return result.Zones is null
                ? Results.Json(new { type = result.Error ?? "UPSTREAM_ERROR" }, statusCode: result.Status)
                : Results.Json(new { zones = result.Zones, page });
    }

    [HttpGet("/hosting/domains/{domain}/dns")]
    public async Task<IResult> hosting_domains_domain_dns_Get_20([FromRoute] string domain)
    {
        var context = HttpContext;
        var service = backend.Dns;
        if (!TryGetBasic(context, out var auth))
                return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);
            var result = await service.ReadAsync(domain, auth, context.RequestAborted);
            return result.Records is null
                ? Results.Json(new { type = result.Error ?? "UPSTREAM_ERROR" }, statusCode: result.Status)
                : Results.Json(new { domain, records = result.Records });
    }

    [HttpPost("/hosting/domains/{domain}/dns")]
    public async Task<IResult> hosting_domains_domain_dns_Post_21([FromRoute] string domain, [FromBody] DnsChange input)
    {
        var context = HttpContext;
        var service = backend.Dns;
        if (!TryGetBasic(context, out var auth))
                return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);
            var result = await service.ChangeAsync(domain, input, auth, context.RequestAborted);
            return result.Error is null
                ? Results.Json(new { domain, action = input.Action, applied = true })
                : Results.Json(new { type = result.Error }, statusCode: result.Status);
    }


}
