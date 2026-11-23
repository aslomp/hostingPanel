using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using HostingPanel.Api.Adapters;
using HostingPanel.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace HostingPanel.Api.Controllers;

[ApiController]
public sealed class HostingController(IHostingBackend backend) : BackendController
{
    [HttpGet("/hosting/accounts")]
    public async Task<IResult> HostingAccountsGet1()
    {
        var context = HttpContext;
        var catalog = backend.AccountCatalog;
        if (!TryGetBasic(context, out var auth))
                return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 40
    }

    [HttpGet("/hosting/domains/accessible")]
    public async Task<IResult> HostingDomainsAccessibleGet2()
    {
        var context = HttpContext;
        var service = backend.AccessibleDomains;
        if (!TryGetBasic(context, out var auth))
                return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 40
    }

    [HttpGet("/hosting/impersonation/check")]
    public async Task<IResult> HostingImpersonationCheckGet5()
    {
        var context = HttpContext;
        var service = backend.Impersonation;
        if (!TryGetBasic(context, out var auth))
                return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 40
    }

    [HttpGet("/hosting/users/{username}/overview")]
    public async Task<IResult> HostingUsersOverviewGet6([FromRoute] string username)
    {
        var context = HttpContext;
        var service = backend.LegacyAccountOverview;
        if (!TryGetBasic(context, out var auth))
                return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 40
    }

    [HttpGet("/hosting/databases")]
    public async Task<IResult> HostingDatabasesGet7()
    {
        var context = HttpContext;
        var service = backend.Databases;
        if (!TryGetBasic(context, out var auth))
                return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 40
    }

    [HttpGet("/hosting/databases/{database}/users")]
    public async Task<IResult> HostingDatabasesUsersGet8([FromRoute] string database)
    {
        var context = HttpContext;
        var service = backend.Databases;
        if (!TryGetBasic(context, out var auth))
                return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 40
    }

    [HttpPost("/hosting/databases")]
    public async Task<IResult> HostingDatabasesPost9([FromBody] DatabaseCreateRequest input)
    {
        var context = HttpContext;
        var service = backend.Databases;
        if (!TryGetBasic(context, out var auth))
                return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 40
    }

    [HttpDelete("/hosting/databases/{database}")]
    public async Task<IResult> HostingDatabasesDelete10([FromRoute] string database, [FromBody] DatabaseDeleteRequest input)
    {
        var context = HttpContext;
        var service = backend.Databases;
        if (!TryGetBasic(context, out var auth))
                return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 40
    }

    [HttpGet("/hosting/domains")]
    public async Task<IResult> HostingDomainsGet11()
    {
        var context = HttpContext;
        var service = backend.Domains;
        if (!TryGetBasic(context, out var auth))
                return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 40
    }

    [HttpPost("/hosting/domains/preview")]
    public IResult HostingDomainsPreviewPost12([FromBody] DomainCreatePreview input)
    {
        var context = HttpContext;
        if (!TryGetBasic(context, out _))
                return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 40
    }

    [HttpPost("/hosting/domains/execute")]
    public async Task<IResult> HostingDomainsExecutePost13([FromBody] DomainMutationRequest input)
    {
        var context = HttpContext;
        var service = backend.Domains;
        if (!TryGetBasic(context, out var auth))
                return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 40
    }

    [HttpGet("/hosting/domains/{domain}")]
    public async Task<IResult> HostingDomainsGet14([FromRoute] string domain)
    {
        var context = HttpContext;
        var service = backend.Domains;
        if (!TryGetBasic(context, out var auth))
                return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 40
    }

    [HttpPut("/hosting/domains/{domain}/settings")]
    public async Task<IResult> HostingDomainsSettingsPut15([FromRoute] string domain, [FromBody] DomainSettingRequest input)
    {
        var context = HttpContext;
        var service = backend.Domains;
        if (!TryGetBasic(context, out var auth))
                return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 40
    }

    [HttpGet("/hosting/dns/zones")]
    public async Task<IResult> HostingDnsZonesGet16()
    {
        var context = HttpContext;
        var service = backend.Dns;
        if (!TryGetBasic(context, out var auth))
                return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 40
    }

    [HttpGet("/hosting/domains/{domain}/dns")]
    public async Task<IResult> HostingDomainsDnsGet17([FromRoute] string domain)
    {
        var context = HttpContext;
        var service = backend.Dns;
        if (!TryGetBasic(context, out var auth))
                return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 40
    }

    [HttpPost("/hosting/domains/{domain}/dns")]
    public async Task<IResult> HostingDomainsDnsPost18([FromRoute] string domain, [FromBody] DnsChange input)
    {
        var context = HttpContext;
        var service = backend.Dns;
        if (!TryGetBasic(context, out var auth))
                return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 40
    }

}
