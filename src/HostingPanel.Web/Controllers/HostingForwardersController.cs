using Microsoft.AspNetCore.Mvc;

namespace HostingPanel.Api.Controllers;

[ApiController]
public sealed class HostingForwardersController(IEnumerable<IMailForwarders> providers) : BackendController
{
    private async Task<IResult> Run(Func<IMailForwarders, System.Net.Http.Headers.AuthenticationHeaderValue, Task<MailForwarderResult>> operation)
    {
        if (!TryGetBasic(HttpContext, out var auth)) return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);
        var provider = providers.SingleOrDefault();
        if (provider is null) return Results.Json(new { type = "MAIL_NOT_SUPPORTED" }, statusCode: 501);
        var result = await operation(provider, auth);
        return result.Error is not null ? Results.Json(new { type = result.Error }, statusCode: result.Status)
            : result.Forwarders is not null ? Results.Json(new { forwarders = result.Forwarders }, statusCode: result.Status)
            : Results.StatusCode(result.Status);
    }

    [HttpGet("/hosting/mail/{domain}/forwarders")]
    public Task<IResult> List(string domain, CancellationToken ct) => Run((p, a) => p.ListForwardersAsync(domain, a, ct));

    [HttpPost("/hosting/mail/{domain}/forwarders")]
    public Task<IResult> Create(string domain, MailForwarder input, CancellationToken ct) => Run((p, a) => p.CreateForwarderAsync(domain, input, a, ct));

    [HttpPut("/hosting/mail/{domain}/forwarders/{name}")]
    public Task<IResult> Update(string domain, string name, MailForwarder input, CancellationToken ct) =>
        Run((p, a) => name == input.Name ? p.UpdateForwarderAsync(domain, input, a, ct)
            : Task.FromResult(new MailForwarderResult(400, "NAME_MISMATCH")));

    [HttpDelete("/hosting/mail/{domain}/forwarders/{name}")]
    public Task<IResult> Delete(string domain, string name, [FromQuery] string confirmation, CancellationToken ct) =>
        Run((p, a) => confirmation == name + "@" + domain ? p.DeleteForwarderAsync(domain, name, a, ct)
            : Task.FromResult(new MailForwarderResult(400, "CONFIRMATION_REQUIRED")));
}
