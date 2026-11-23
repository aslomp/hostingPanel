using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using HostingPanel.Api.Adapters;
using HostingPanel.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace HostingPanel.Api.Controllers;

[ApiController]
public sealed class DomainController(IHostingBackend backend) : BackendController
{
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
                });
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
            });
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

    [HttpGet("/api/domain-tls/domains")]
    [HttpGet("/api/domain-tls/domains/{domain}/acme-config")]
    [HttpPut("/api/domain-tls/domains/{domain}/acme-config")]
    [HttpGet("/api/domain-tls/domains/{domain}/acme-logs")]
    [HttpGet("/api/domain-tls/domains/{domain}/acme-logs/{log_id}")]
    [HttpGet("/api/domain-tls/domains/{domain}/certs")]
    [HttpDelete("/api/domain-tls/domains/{domain}/certs/{id}")]
    [HttpPost("/api/domain-tls/domains/{domain}/certs/{id}/create-csr")]
    [HttpGet("/api/domain-tls/domains/{domain}/certs/{id}/files")]
    [HttpPut("/api/domain-tls/domains/{domain}/certs/{id}/files")]
    [HttpPost("/api/domain-tls/domains/{domain}/certs/{id}/install-self-signed")]
    [HttpPost("/api/domain-tls/domains/{domain}/provision-certs")]
    [HttpPost("/api/domain-tls/domains/{domain}/provision-certs-dry-run")]
    public Task ForwardDomainAsync() => ForwardToBackendAsync(HttpContext, backend.Clients);


}
