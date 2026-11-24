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

        var result = await backend.Dns.ListAccessibleZonesAsync(auth, ct);
        return result.Zones is null
            ? Results.Json(new { type = result.Warning ?? "DNS_INVENTORY_UNAVAILABLE" }, statusCode: result.Status)
            : Results.Json(new { zones = result.Zones, warning = result.Warning, complete = result.Warning is null },
                statusCode: result.Status);
    }

    [HttpPost("/hosting/dns/zones")]
    public async Task<IResult> CreateDnsZone([FromBody] DnsZoneCreate input, CancellationToken ct)
    {
        if (!TryGetBasic(HttpContext, out var auth))
            return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);
        var result = await backend.Dns.CreateZoneAsync(input, auth, ct);
        return result.Error is null
            ? Results.Json(new { domain = input.Domain, created = result.Executed, provisioningPending = result.ProvisioningPending }, statusCode: result.Status)
            : Results.Json(new { type = result.Error }, statusCode: result.Status);
    }

    [HttpDelete("/hosting/dns/zones/{domain}")]
    public async Task<IResult> DeleteDnsZone([FromRoute] string domain,
        [FromBody] DnsZoneDeletion input, CancellationToken ct)
    {
        if (!TryGetBasic(HttpContext, out var auth))
            return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);
        var result = await backend.Dns.DeleteZoneAsync(domain, input.Confirmation, auth, ct);
        return result.Error is null
            ? Results.Json(new { domain, deleted = result.Executed, provisioningPending = result.ProvisioningPending }, statusCode: result.Status)
            : Results.Json(new { type = result.Error }, statusCode: result.Status);
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
                ? Results.Json(new { domain, action = input.Action, applied = result.Executed, provisioningPending = result.ProvisioningPending }, statusCode: result.Status)
                : Results.Json(new { type = result.Error }, statusCode: result.Status);
    }


}


public sealed record DnsZoneDeletion(string Confirmation);
