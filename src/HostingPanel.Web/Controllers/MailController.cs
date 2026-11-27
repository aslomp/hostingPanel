using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http.Extensions;

namespace HostingPanel.Api.Controllers;

[ApiController]
public sealed class MailController(IEnumerable<IMailProvider> providers, IApiCompatibilityHandler compatibility, IConfiguration configuration) : BackendController
{
    private static IResult Encoded(IEnumerable<KeyValuePair<string, string?>> fields, int status = 200) =>
        Results.Text(new QueryBuilder(fields.Select(x => new KeyValuePair<string, string>(x.Key, x.Value ?? ""))).ToQueryString().Value?.TrimStart('?') ?? "",
            "application/x-www-form-urlencoded", statusCode: status);
    private static IResult Error(string code, int status = 200) =>
        Encoded(new Dictionary<string, string?> { ["error"] = "1", ["text"] = code, ["details"] = "" }, status);
    private static IResult Outcome(MailResult result) => result.Error is null
        ? Encoded(new Dictionary<string, string?> { ["error"] = "0", ["text"] = "", ["details"] = "" })
        : Error(result.Error, result.Status is 401 or 403 or 501 or 502 or 503 ? result.Status : 200);

    [HttpGet("/CMD_API_POP")]
    [HttpPost("/CMD_API_POP")]
    public async Task<IResult> Pop(CancellationToken ct)
    {
        if (!TryGetBasic(HttpContext, out var auth)) return Error("UNAUTHORIZED", 401);
        var provider = providers.SingleOrDefault();
        if (string.Equals(configuration["HostingPanel:Backend"] ?? "DirectAdmin", "DirectAdmin", StringComparison.OrdinalIgnoreCase))
        {
            await compatibility.ForwardAsync(HttpContext);
            return Results.Empty;
        }
        if (provider is null) return Error("MAIL_NOT_SUPPORTED", 501);
        var post = HttpMethods.IsPost(Request.Method);
        if (post && !Request.HasFormContentType) return Error("INVALID_FORM", 400);
        var form = post ? await Request.ReadFormAsync(ct) : null;
        string Read(string key) => (post ? form![key] : Request.Query[key]).ToString();
        var action = Read("action");
        var domain = Read("domain");
        var user = Read("user");
        if (string.IsNullOrWhiteSpace(domain)) return Error("INVALID_DOMAIN");
        if (!post)
        {
            if (action != "list") return Error("UNSUPPORTED_MAIL_OPERATION", 501);
            var result = await provider.ListAsync(domain, auth, ct);
            if (result.Error is not null) return Outcome(result);
            return Encoded((result.Accounts ?? []).Select(x => new KeyValuePair<string, string?>("list[]", x.Name)));
        }
        if (action == "delete")
        {
            if (string.IsNullOrWhiteSpace(user) || form!.Keys.Any(k => k.StartsWith("select", StringComparison.OrdinalIgnoreCase)))
                return Error("UNSUPPORTED_MAIL_OPERATION", 501);
            return Outcome(await provider.DeleteAsync(domain, user, auth, ct));
        }
        if (action is not ("create" or "modify")) return Error("UNSUPPORTED_MAIL_OPERATION", 501);
        if (Read("passwd_is_crypted") != "" || Read("limit") != "" || Read("newuser") != "")
            return Error("UNSUPPORTED_MAIL_OPERATION", 501);
        var password = Read("passwd");
        if (password != Read("passwd2")) return Error("PASSWORD_MISMATCH");
        if (!long.TryParse(Read("quota"), NumberStyles.None, CultureInfo.InvariantCulture, out var quota) || quota <= 0)
            return Error("INVALID_OR_UNSUPPORTED_QUOTA");
        if (action == "create")
        {
            if (quota > long.MaxValue / 1048576) return Error("INVALID_QUOTA");
            return Outcome(await provider.CreateAsync(domain, new(user, password, quota * 1048576), auth, ct));
        }
        return Outcome(await provider.UpdateAsync(domain, user, new(password.Length == 0 ? null : password, quota), auth, ct));
    }
}
