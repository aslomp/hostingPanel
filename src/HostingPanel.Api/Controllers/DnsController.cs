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

        var zones = new Dictionary<string, AccessibleDnsZone>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        var adminStatus = 200;

        // DirectAdmin may host standalone DNS zones that have no hosting domain.
        // Discover those from the admin DNS interface, not just account domains.
        // No assumed admin/reseller role and no locally constructed permission tree.
        var lastPage = 0;
        for (var page = 1; page <= 10000; page++)
        {
            var listed = await backend.Dns.ListAdminZonesAsync(auth, page, ct);
            if (listed.Zones is null)
            {
                adminStatus = listed.Status;
                if (page > 1 || listed.Status is not (401 or 403 or 404))
                    warnings.Add("ADMIN_DNS_LIST_UNAVAILABLE");
                break;
            }
            if (listed.Zones.Count == 0) { lastPage = page; break; }

            var fresh = 0;
            foreach (var zone in listed.Zones)
            {
                if (zones.TryAdd(zone.Domain, new AccessibleDnsZone(
                    zone.Domain, zone.LocalData, zone.LocalMail, null)))
                    fresh++;
            }

            if (fresh == 0)
            {
                // A legacy backend may ignore page= and repeat the same result.
                warnings.Add("ADMIN_DNS_PAGINATION_UNVERIFIED");
                break;
            }
            if (page == 10000)
                warnings.Add("ADMIN_DNS_PAGE_LIMIT");
        }

        // Supplement DNS-only results with resources discoverable through the
        // caller's accessible account hierarchy. Neither source is a role check.
        var accessible = await backend.AccessibleDomains.ListAsync(auth, ct);
        if (accessible.Items is not null)
        {
            foreach (var item in accessible.Items)
            {
                if (!zones.ContainsKey(item.Domain))
                    zones[item.Domain] = new AccessibleDnsZone(item.Domain, null, null, item.Owner);
                else if (string.IsNullOrWhiteSpace(zones[item.Domain].Owner))
                    zones[item.Domain] = zones[item.Domain] with { Owner = item.Owner };
            }
            if (accessible.Warning is not null) warnings.Add(accessible.Warning);
        }
        else
        {
            warnings.Add(accessible.Warning ?? "DOMAIN_DISCOVERY_UNAVAILABLE");
        }

        if (zones.Count == 0 && accessible.Items is null)
            return Results.Json(new { type = "DNS_INVENTORY_UNAVAILABLE" },
                statusCode: adminStatus is 401 or 403 ? adminStatus : accessible.Status);

        return Results.Json(new
        {
            zones = zones.Values.OrderBy(z => z.Domain, StringComparer.OrdinalIgnoreCase),
            warning = warnings.Count == 0 ? null : string.Join(",", warnings.Distinct()),
            complete = warnings.Count == 0
        });
    }

    [HttpPost("/hosting/dns/zones")]
    public async Task<IResult> CreateDnsZone([FromBody] DnsZoneCreate input, CancellationToken ct)
    {
        if (!TryGetBasic(HttpContext, out var auth))
            return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);
        var result = await backend.Dns.CreateZoneAsync(input, auth, ct);
        return result.Error is null
            ? Results.Json(new { domain = input.Domain, created = true }, statusCode: result.Status)
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
            ? Results.Json(new { domain, deleted = true }, statusCode: result.Status)
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
                ? Results.Json(new { domain, action = input.Action, applied = true }, statusCode: result.Status)
                : Results.Json(new { type = result.Error }, statusCode: result.Status);
    }


}

public sealed record AccessibleDnsZone(string Domain, bool? LocalData, bool? LocalMail, string? Owner);

public sealed record DnsZoneDeletion(string Confirmation);
