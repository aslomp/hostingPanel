using Microsoft.AspNetCore.Mvc;

namespace HostingPanel.Api.Controllers;

[ApiController]
public sealed class HostingMailController(IEnumerable<IMailProvider> providers) : BackendController
{
    private async Task<IResult> Run(Func<IMailProvider, System.Net.Http.Headers.AuthenticationHeaderValue, Task<MailResult>> operation)
    {
        if (!TryGetBasic(HttpContext, out var auth))
            return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);
        var provider = providers.SingleOrDefault();
        if (provider is null)
            return Results.Json(new { type = "MAIL_NOT_SUPPORTED" }, statusCode: 501);
        var result = await operation(provider, auth);
        if (result.Error is not null)
            return Results.Json(new { type = result.Error }, statusCode: result.Status);
        return result.Accounts is not null
            ? Results.Json(new { accounts = result.Accounts }, statusCode: result.Status)
            : Results.StatusCode(result.Status);
    }

    [HttpGet("/hosting/mail/{domain}/accounts")]
    public Task<IResult> List(string domain, CancellationToken ct) =>
        Run((p, a) => p.ListAsync(domain, a, ct));

    [HttpPost("/hosting/mail/{domain}/accounts")]
    public Task<IResult> Create(string domain, MailCreateRequest input, CancellationToken ct) =>
        Run((p, a) => p.CreateAsync(domain, input, a, ct));

    [HttpPatch("/hosting/mail/{domain}/accounts/{name}")]
    public Task<IResult> Update(string domain, string name, MailUpdateRequest input, CancellationToken ct) =>
        Run((p, a) => p.UpdateAsync(domain, name, input, a, ct));

    [HttpDelete("/hosting/mail/{domain}/accounts/{name}")]
    public Task<IResult> Delete(string domain, string name, [FromQuery] string confirmation, CancellationToken ct) =>
        Run((p, a) => string.Equals(confirmation, name + "@" + domain, StringComparison.Ordinal)
            ? p.DeleteAsync(domain, name, a, ct)
            : Task.FromResult(new MailResult(400, "CONFIRMATION_REQUIRED")));
}
