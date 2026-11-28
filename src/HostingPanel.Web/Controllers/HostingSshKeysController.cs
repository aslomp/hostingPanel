using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc;

namespace HostingPanel.Api.Controllers;

[ApiController]
public sealed class HostingSshKeysController(IEnumerable<ISshAuthorizedKeys> providers) : BackendController
{
    public sealed record KeyInput(string PublicKey);
    public sealed record RevokeInput(string Fingerprint, string Confirmation);
    private async Task<IResult> Run(Func<ISshAuthorizedKeys, AuthenticationHeaderValue, Task<SshKeyResult>> operation)
    {
        if (!TryGetBasic(HttpContext, out var auth)) return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);
        var provider = providers.SingleOrDefault();
        if (provider is null) return Results.Json(new { type = "SSH_KEYS_NOT_SUPPORTED" }, statusCode: 501);
        var result = await operation(provider, auth);
        return result.Error is not null ? Results.Json(new { type = result.Error }, statusCode: result.Status)
            : result.Keys is not null ? Results.Json(new { keys = result.Keys }, statusCode: result.Status) : Results.StatusCode(result.Status);
    }
    [HttpGet("/hosting/file-access/ssh-keys")]
    public Task<IResult> List(CancellationToken ct) => Run((p,a) => p.ListAsync(a,ct));
    [HttpPost("/hosting/file-access/ssh-keys")]
    public Task<IResult> Authorize(KeyInput input, CancellationToken ct) => Run((p,a) => p.AuthorizeAsync(input.PublicKey,a,ct));
    [HttpPost("/hosting/file-access/ssh-keys/revoke")]
    public Task<IResult> Revoke(RevokeInput input, CancellationToken ct) => Run((p,a) => input.Fingerprint == input.Confirmation
        ? p.RevokeAsync(input.Fingerprint,a,ct) : Task.FromResult(new SshKeyResult(400,"CONFIRMATION_REQUIRED")));
}
