using Microsoft.AspNetCore.Mvc;

namespace HostingPanel.Api.Controllers;

[ApiController]
[Route("/hosting/mail/{domain}/accounts")]
public sealed class MailController(IEnumerable<IMailProvider> providers) : BackendController
{
    private async Task<IResult> Run(Func<IMailProvider, System.Net.Http.Headers.AuthenticationHeaderValue, Task<MailResult>> operation)
    {
        if (!TryGetBasic(HttpContext, out var auth)) return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);
        var provider = providers.SingleOrDefault();
        if (provider is null) return Results.Json(new { type = "MAIL_NOT_SUPPORTED" }, statusCode: 501);
        var result = await operation(provider, auth);
        return result.Error is not null ? Results.Json(new { type = result.Error }, statusCode: result.Status)
            : Results.Json(new { accounts = result.Accounts }, statusCode: result.Status);
    }

    [HttpGet]
    public Task<IResult> List(string domain, CancellationToken ct) => Run((p, a) => p.ListAsync(domain, a, ct));
    [HttpPost]
    public Task<IResult> Create(string domain, MailCreateRequest input, CancellationToken ct) => Run((p, a) => p.CreateAsync(domain, input, a, ct));
    [HttpPatch("{name}")]
    public Task<IResult> Update(string domain, string name, MailUpdateRequest input, CancellationToken ct) => Run((p, a) => p.UpdateAsync(domain, name, input, a, ct));
    [HttpDelete("{name}")]
    public Task<IResult> Delete(string domain, string name, [FromQuery] string confirmation, CancellationToken ct) =>
        confirmation == name + "@" + domain ? Run((p, a) => p.DeleteAsync(domain, name, a, ct))
            : Task.FromResult<IResult>(Results.Json(new { type = "CONFIRMATION_REQUIRED" }, statusCode: 400));
}
