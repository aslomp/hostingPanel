using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using HostingPanel.Api.Adapters;
using HostingPanel.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace HostingPanel.Api.Controllers;

[ApiController]
public sealed class UserController(IHostingBackend backend) : BackendController
{
    [HttpGet("/api/search/users")]
    public async Task<IResult> api_search_users_Get_3()
    {
        var context = HttpContext;
        var catalog = backend.AccountCatalog;
        if (!TryGetBasic(context, out var authentication))
                return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);
            var query = context.Request.Query["q"].ToString();
            var rawLimit = context.Request.Query["limit"].ToString();
            var limit = 50;
            if (rawLimit.Length > 0 && (!int.TryParse(rawLimit, out limit) || limit is < 1 or > 1000))
                return Results.Json(new { type = "BAD_REQUEST" }, statusCode: 400);
            if (query.Length > 256)
                return Results.Json(new { type = "BAD_REQUEST" }, statusCode: 400);
        
            var listing = await catalog.ListAsync(authentication, context.RequestAborted);
            return listing.Accounts is null
                ? Results.Json(new { type = listing.Error ?? "UPSTREAM_ERROR" }, statusCode: listing.Status)
                : Results.Json(listing.Accounts.Select(x => x.Username)
                    .Where(x => string.IsNullOrEmpty(query) ||
                        x.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .Take(limit).ToArray());
    }

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

    [HttpGet("/api/users/{username}/config")]
    public Task<IResult> api_users_username_config_Get_6([FromRoute] string username)
    {
        var context = HttpContext;
        var service = backend.LegacyUserDetails;
        return ReadUserDetail(context, username, "config", service);
    }

    [HttpGet("/api/users/{username}/usage")]
    public Task<IResult> api_users_username_usage_Get_7([FromRoute] string username)
    {
        var context = HttpContext;
        var service = backend.LegacyUserDetails;
        return ReadUserDetail(context, username, "usage", service);
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
                });
    }

    [HttpGet("/api/search/users-extended")]
    [HttpGet("/api/users/list")]
    [HttpGet("/api/users/{username}/login-history")]
    public Task ForwardUserAsync() => ForwardToBackendAsync(HttpContext, backend.Clients);

    static async Task<IResult> ReadUserDetail(HttpContext context, string username,
        string kind, LegacyUserDetailsService service)
    {
        if (!TryGetBasic(context, out var auth))
            return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);
        if (username.Length is < 1 or > 64 || !username.All(c =>
            char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
            return Results.Json(new { type = "BAD_REQUEST" }, statusCode: 400);
        var result = await service.GetAsync(username, kind, auth, context.RequestAborted);
        return result.Result is not null
            ? Results.Json(result.Result)
            : Results.Json(new { type = result.Error ?? "UPSTREAM_ERROR" }, statusCode: result.Status);
    }
}
