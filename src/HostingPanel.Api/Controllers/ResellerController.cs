using HostingPanel.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace HostingPanel.Api.Controllers;

/// <summary>Exact DirectAdmin Swagger routes. Backend determines authorization and support.</summary>
[ApiController]
public sealed class ResellerController(IHostingBackend backend) : BackendController
{
    [HttpPut("/hosting/resellers/{username}/limit")]
    public async Task<IResult> SetResellerLimit([FromRoute] string username,
        [FromBody] AccountLimitChange input, CancellationToken ct)
    {
        if (!TryGetBasic(HttpContext, out var auth))
            return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);
        var result = await backend.AccountMutations.SetLimitAsync(
            "reseller", username, input, auth, ct);
        return result.Error is null
            ? Results.Json(new { username, field = input.Field, updated = true })
            : Results.Json(new { type = result.Error }, statusCode: result.Status);
    }

    [HttpPost("/hosting/resellers")]
    public async Task<IResult> CreateReseller([FromBody] CreateHostingAccount input, CancellationToken ct)
    {
        if (!TryGetBasic(HttpContext, out var auth))
            return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);
        if (input.Kind != "reseller")
            return Results.Json(new { type = "INVALID_ACCOUNT_KIND" }, statusCode: 400);
        var result = await backend.AccountMutations.CreateAsync(input, auth, ct);
        return result.Error is null
            ? Results.Json(new { username = input.Username, created = true })
            : Results.Json(new { type = result.Error }, statusCode: result.Status);
    }

    [HttpDelete("/hosting/resellers/{username}")]
    public async Task<IResult> DeleteReseller(
        [FromRoute] string username, [FromBody] AccountDeletion input, CancellationToken ct)
    {
        if (!TryGetBasic(HttpContext, out var auth))
            return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);
        var result = await backend.AccountMutations.DeleteAsync(username, input.Confirmation, auth, ct);
        return result.Error is null
            ? Results.Json(new { username, deleted = true })
            : Results.Json(new { type = result.Error }, statusCode: result.Status);
    }

    [HttpGet("/api/resellers/{username}/config")]
    [HttpGet("/api/resellers/{username}/usage")]
    public Task ForwardAsync() => ForwardToBackendAsync(HttpContext, backend.Clients);
}
