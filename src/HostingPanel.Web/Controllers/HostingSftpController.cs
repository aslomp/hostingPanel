using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc;

namespace HostingPanel.Api.Controllers;

[ApiController]
public sealed class HostingSftpController(IEnumerable<ISftpAccounts> providers) : BackendController
{
    private async Task<IResult> Run(Func<ISftpAccounts, AuthenticationHeaderValue, Task<SftpAccountResult>> operation)
    {
        if (!TryGetBasic(HttpContext, out var auth))
            return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);
        var provider = providers.SingleOrDefault();
        if (provider is null)
            return Results.Json(new { type = "SFTP_NOT_SUPPORTED" }, statusCode: 501);
        var result = await operation(provider, auth);
        if (result.Error is not null)
            return Results.Json(new { type = result.Error }, statusCode: result.Status);
        return result.Accounts is not null
            ? Results.Json(new { accounts = result.Accounts, limit = result.Limit == long.MaxValue ? (long?)null : result.Limit,
                unlimited = result.Limit == long.MaxValue }, statusCode: result.Status)
            : Results.StatusCode(result.Status);
    }

    [HttpGet("/hosting/file-access/sftp")]
    public Task<IResult> List(CancellationToken ct) => Run((p, a) => p.ListAsync(a, ct));

    [HttpPost("/hosting/file-access/sftp")]
    public Task<IResult> Create([FromBody] SftpAccountRequest input, CancellationToken ct) =>
        Run((p, a) => p.CreateAsync(input, a, ct));

    [HttpPatch("/hosting/file-access/sftp/{id:guid}")]
    public Task<IResult> Update(Guid id, [FromBody] SftpAccountUpdate input, CancellationToken ct) =>
        Run((p, a) => p.UpdateAsync(id, input, a, ct));

    [HttpDelete("/hosting/file-access/sftp/{id:guid}")]
    public Task<IResult> Delete(Guid id, [FromQuery] string confirmation, CancellationToken ct) =>
        Run(async (p, a) => {
            var inventory = await p.ListAsync(a, ct);
            if (inventory.Status != 200) return inventory;
            var account = inventory.Accounts?.SingleOrDefault(x => x.Id == id);
            if (account is null) return new(404, "SFTP_ACCOUNT_NOT_FOUND");
            if (!string.Equals(confirmation, account.Username, StringComparison.Ordinal))
                return new(400, "CONFIRMATION_REQUIRED");
            return await p.DeleteAsync(id, a, ct);
        });
}
