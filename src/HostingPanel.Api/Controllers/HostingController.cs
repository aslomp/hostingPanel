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
    public async Task<IResult> hosting_accounts_Get_4()
    {
        var context = HttpContext;
        var catalog = backend.AccountCatalog;
        if (!TryGetBasic(context, out var auth))
                return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);
            var result = await catalog.ListAsync(auth, context.RequestAborted);
            return result.Accounts is null
                ? Results.Json(new { type = result.Error ?? "UPSTREAM_ERROR" }, statusCode: result.Status)
                : Results.Json(new { accounts = result.Accounts });
    }

    [HttpGet("/hosting/domains/accessible")]
    public async Task<IResult> hosting_domains_accessible_Get_5()
    {
        var context = HttpContext;
        var service = backend.AccessibleDomains;
        if (!TryGetBasic(context, out var auth))
                return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);
            var result = await service.ListAsync(auth, context.RequestAborted);
            return result.Items is null
                ? Results.Json(new { type = result.Warning ?? "UPSTREAM_ERROR" }, statusCode: result.Status)
                : Results.Json(new {
                    items = result.Items, domains = result.Items.Select(x => x.Domain).ToArray(),
                    warning = result.Warning
    }

    [HttpGet("/hosting/impersonation/check")]
    public async Task<IResult> hosting_impersonation_check_Get_8()
    {
        var context = HttpContext;
        var service = backend.Impersonation;
        if (!TryGetBasic(context, out var auth))
                return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);
            var target = context.Request.Query["user"].ToString();
            var result = await service.CheckAsync(auth, target, context.RequestAborted);
            return result.Error is null
                ? Results.Json(new { allowed = true, effectiveUsername = target })
                : Results.Json(new { type = result.Error }, statusCode: result.Status);
    }

    [HttpGet("/hosting/users/{username}/overview")]
    public async Task<IResult> hosting_users_username_overview_Get_9([FromRoute] string username)
    {
        var context = HttpContext;
        var service = backend.LegacyAccountOverview;
        if (!TryGetBasic(context, out var auth))
                return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);
            if (!LegacyAccountOverviewService.ValidUsername(username))
                return Results.Json(new { type = "INVALID_USERNAME" }, statusCode: 400);
            var result = await service.GetAsync(username, auth, context.RequestAborted);
            return result.Config is null
                ? Results.Json(new { type = result.Error ?? "UPSTREAM_ERROR" }, statusCode: result.Status)
                : Results.Json(new {
                    username, config = result.Config,
                    package = result.Package, packageSettings = result.PackageSettings,
                    packageError = result.Error, usage = result.Usage, usageError = result.UsageError
    }

    [HttpGet("/hosting/databases")]
    public async Task<IResult> hosting_databases_Get_10()
    {
        var context = HttpContext;
        var service = backend.Databases;
        if (!TryGetBasic(context, out var auth))
                return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);
            var result = await service.ListAsync(auth, context.RequestAborted);
            return result.Databases is null
                ? Results.Json(new { type = result.Error ?? "UPSTREAM_ERROR" }, statusCode: result.Status)
                : Results.Json(new { databases = result.Databases });
    }

    [HttpGet("/hosting/databases/{database}/users")]
    public async Task<IResult> hosting_databases_database_users_Get_11([FromRoute] string database)
    {
        var context = HttpContext;
        var service = backend.Databases;
        if (!TryGetBasic(context, out var auth))
                return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);
            var result = await service.UsersAsync(database, auth, context.RequestAborted);
            return result.Users is null
                ? Results.Json(new { type = result.Error ?? "UPSTREAM_ERROR" }, statusCode: result.Status)
                : Results.Json(new { database, users = result.Users });
    }

    [HttpPost("/hosting/databases")]
    public async Task<IResult> hosting_databases_Post_12([FromBody] DatabaseCreateRequest input)
    {
        var context = HttpContext;
        var service = backend.Databases;
        if (!TryGetBasic(context, out var auth))
                return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);
            var result = await service.CreateAsync(input, auth, context.RequestAborted);
            return result.Error is null
                ? Results.Json(new { created = true })
                : Results.Json(new { type = result.Error }, statusCode: result.Status);
    }

    [HttpDelete("/hosting/databases/{database}")]
    public async Task<IResult> hosting_databases_database_Delete_13([FromRoute] string database, [FromBody] DatabaseDeleteRequest input)
    {
        var context = HttpContext;
        var service = backend.Databases;
        if (!TryGetBasic(context, out var auth))
                return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);
            var result = await service.DeleteAsync(database, input.Confirmation, auth,
                context.RequestAborted);
            return result.Error is null
                ? Results.Json(new { database, deleted = true })
                : Results.Json(new { type = result.Error }, statusCode: result.Status);
    }

    [HttpGet("/hosting/domains")]
    public async Task<IResult> hosting_domains_Get_14()
    {
        var context = HttpContext;
        var service = backend.Domains;
        if (!TryGetBasic(context, out var auth))
                return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);
            var owner = context.Request.Query["user"].ToString();
            var selected = string.IsNullOrWhiteSpace(owner) ? null : owner;
            if (!DomainManagementService.ValidOwner(selected))
                return Results.Json(new { type = "BAD_REQUEST" }, statusCode: 400);
        
            var response = await service.ListAsync(selected, auth, context.RequestAborted);
            return response.Domains is not null
                ? Results.Json(new { owner = selected, domains = response.Domains })
                : Results.Json(new { type = response.Error ?? "UPSTREAM_ERROR" }, statusCode: response.Status);
    }

    [HttpPost("/hosting/domains/preview")]
    public IResult hosting_domains_preview_Post_15([FromBody] DomainCreatePreview input)
    {
        var context = HttpContext;
        if (!TryGetBasic(context, out _))
                return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);
            if (!LegacyDomainsAdapter.IsValidDomain(input.Domain))
                return Results.Json(new { type = "INVALID_DOMAIN" }, statusCode: 400);
            return Results.Json(new {
                domain = input.Domain.ToLowerInvariant(),
                dryRun = true,
                legacyEndpoint = "CMD_API_DOMAIN",
                method = "POST",
                form = LegacyDomainsAdapter.EncodeCreateForm(input.Domain.ToLowerInvariant(), input.Php, input.Ssl, input.Cgi),
                executed = false
    }

    [HttpPost("/hosting/domains/execute")]
    public async Task<IResult> hosting_domains_execute_Post_16([FromBody] DomainMutationRequest input)
    {
        var context = HttpContext;
        var service = backend.Domains;
        if (!TryGetBasic(context, out var auth))
                return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);
            var result = await service.MutateAsync(input, auth, context.RequestAborted);
            return result.Error is null
                ? Results.Json(new { domain = input.Domain.ToLowerInvariant(), action = input.Action, executed = true })
                : Results.Json(new { type = result.Error }, statusCode: result.Status);
    }

    [HttpGet("/hosting/domains/{domain}")]
    public async Task<IResult> hosting_domains_domain_Get_17([FromRoute] string domain)
    {
        var context = HttpContext;
        var service = backend.Domains;
        if (!TryGetBasic(context, out var auth))
                return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);
            var result = await service.GetDetailsAsync(domain, auth, context.RequestAborted);
            return result.Settings is not null
                ? Results.Json(new { domain, settings = result.Settings })
                : Results.Json(new { type = result.Error ?? "UPSTREAM_ERROR" }, statusCode: result.Status);
    }

    [HttpPut("/hosting/domains/{domain}/settings")]
    public async Task<IResult> hosting_domains_domain_settings_Put_18([FromRoute] string domain, [FromBody] DomainSettingRequest input)
    {
        var context = HttpContext;
        var service = backend.Domains;
        if (!TryGetBasic(context, out var auth))
                return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);
            var result = await service.UpdateSettingAsync(domain, input.Setting, input.Value,
                auth, context.RequestAborted);
            return result.Error is null
                ? Results.Json(new { domain, setting = input.Setting, updated = true })
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
                ? Results.Json(new { domain, action = input.Action, applied = true })
                : Results.Json(new { type = result.Error }, statusCode: result.Status);
    }

}
