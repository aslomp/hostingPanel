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
    [HttpPost("/hosting/users")]
    public async Task<IResult> CreateHostingUser([FromBody] CreateHostingAccount input, CancellationToken ct)
    {
        if (!TryGetBasic(HttpContext, out var auth))
            return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);
        if (input.Kind != "user")
            return Results.Json(new { type = "INVALID_ACCOUNT_KIND" }, statusCode: 400);
        var result = await backend.AccountMutations.CreateAsync(input, auth, ct);
        return result.Error is null
            ? Results.Json(new { username = input.Username, created = true })
            : Results.Json(new { type = result.Error }, statusCode: result.Status);
    }

    [HttpDelete("/hosting/users/{username}")]
    public async Task<IResult> DeleteHostingUser(
        [FromRoute] string username, [FromBody] AccountDeletion input, CancellationToken ct)
    {
        if (!TryGetBasic(HttpContext, out var auth))
            return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);
        var result = await backend.AccountMutations.DeleteAsync(username, input.Confirmation, auth, ct);
        return result.Error is null
            ? Results.Json(new { username, deleted = true })
            : Results.Json(new { type = result.Error }, statusCode: result.Status);
    }

    [HttpPut("/hosting/users/{username}/package")]
    public async Task<IResult> ChangeUserPackage([FromRoute] string username,
        [FromBody] AccountPackageChange input, CancellationToken ct)
    {
        if (!TryGetBasic(HttpContext, out var auth))
            return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);
        var result = await backend.AccountMutations.SetPackageAsync(
            username, input.Package, auth, ct);
        return result.Error is null
            ? Results.Json(new { username, package = input.Package, updated = true })
            : Results.Json(new { type = result.Error }, statusCode: result.Status);
    }

    [HttpPut("/hosting/users/{username}/limit")]
    public async Task<IResult> SetUserLimit([FromRoute] string username,
        [FromBody] AccountLimitChange input, CancellationToken ct)
    {
        if (!TryGetBasic(HttpContext, out var auth))
            return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);
        var result = await backend.AccountMutations.SetLimitAsync(
            "user", username, input, auth, ct);
        return result.Error is null
            ? Results.Json(new { username, field = input.Field, updated = true })
            : Results.Json(new { type = result.Error }, statusCode: result.Status);
    }

    [HttpPut("/hosting/accounts/{username}/suspension")]
    public async Task<IResult> SetAccountSuspension([FromRoute] string username,
        [FromBody] AccountSuspension input, CancellationToken ct)
    {
        if (!TryGetBasic(HttpContext, out var auth))
            return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);
        var result = await backend.AccountMutations.SetSuspendedAsync(
            username, input.Suspended, auth, ct);
        return result.Error is null
            ? Results.Json(new { username, suspended = input.Suspended, updated = true })
            : Results.Json(new { type = result.Error }, statusCode: result.Status);
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
        var service = backend.AccountOverview;
        if (!TryGetBasic(context, out var auth))
                return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);
            if (!HostingInputValidation.ValidUsername(username))
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

    [HttpGet("/api/search/users")]
    [HttpGet("/api/users/{username}/config")]
    [HttpGet("/api/users/{username}/usage")]
    [HttpGet("/api/search/users-extended")]
    [HttpGet("/api/users/list")]
    [HttpGet("/api/users/{username}/login-history")]
    public Task ForwardUserAsync() => backend.ForwardAsync(HttpContext);


}

public sealed record AccountPackageChange(string Package);
