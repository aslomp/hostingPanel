using System.Net.Http.Headers;
using HostingPanel.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace HostingPanel.Api;
public sealed class KubernetesFtpCompatibility(IFileAccess access)
{
    public async Task ForwardAsync(HttpContext context)
    {
        var request = context.Request;
        var ct = context.RequestAborted;
        async Task Write(FileAccessResult result)
        {
            context.Response.StatusCode = result.Status;
            context.Response.ContentType = "application/x-www-form-urlencoded; charset=utf-8";
            await context.Response.WriteAsync(result.Body, ct);
        }
        if (!AuthenticationHeaderValue.TryParse(request.Headers.Authorization.ToString(),out var auth) || !auth.Scheme.Equals("Basic",StringComparison.OrdinalIgnoreCase))
        { await Write(new(401,"error=1&text=UNAUTHORIZED")); return; }
        var fields = new Dictionary<string,StringValues>(StringComparer.OrdinalIgnoreCase);
        bool Collect(IEnumerable<KeyValuePair<string,StringValues>> source)
        {
            foreach(var pair in source) if(pair.Value.Count != 1 || !fields.TryAdd(pair.Key,pair.Value)) return false;
            return true;
        }
        if (!Collect(request.Query)) { await Write(new(400,"error=1&text=AMBIGUOUS_FORM")); return; }
        if (HttpMethods.IsPost(request.Method) && (request.HasFormContentType || request.ContentLength is > 0 || request.Headers.ContainsKey("Transfer-Encoding")))
        {
            try { if (!request.HasFormContentType || !Collect(await request.ReadFormAsync(ct))) { await Write(new(400,"error=1&text=INVALID_FORM")); return; } }
            catch(Exception ex) when(ex is InvalidDataException or BadHttpRequestException) { await Write(new(400,"error=1&text=INVALID_FORM")); return; }
        }
        string Read(string key) => fields.GetValueOrDefault(key).ToString();
        bool Only(params string[] allowed) => fields.Keys.All(k => allowed.Contains(k,StringComparer.OrdinalIgnoreCase));
        var domain = Read("domain"); var user = Read("user"); var action = Read("action");
        if (HttpMethods.IsGet(request.Method) && action == "" && request.Path.Value!.EndsWith("_SHOW",StringComparison.OrdinalIgnoreCase) && Only("domain","user"))
            await Write(await access.GetFtpAsync(domain,user,auth,ct));
        else if (action == "" && Only("domain")) await Write(await access.ListFtpAsync(domain,auth,ct));
        else if (HttpMethods.IsPost(request.Method) && action == "delete" && Only("domain","action","select0")) await Write(await access.DeleteFtpAsync(domain,Read("select0"),auth,ct));
        else if (HttpMethods.IsPost(request.Method) && action is "create" or "modify" && Only("domain","action","user","type","custom_val","passwd","passwd2","public_keys"))
        {
            if (Read("passwd") != "" || Read("passwd2") != "") { await Write(new(400,"error=1&text=SFTP_PASSWORD_NOT_SUPPORTED")); return; }
            var keys = fields.ContainsKey("public_keys") ? Read("public_keys").Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : null;
            var input = new FtpAccountRequest(domain,user,Read("type"),"",Read("custom_val"),keys);
            await Write(action == "create" ? await access.CreateFtpAsync(input,auth,ct) : await access.UpdateFtpAsync(user,input,auth,ct));
        }
        else await Write(new(501,"error=1&text=UNSUPPORTED_FTP_OPERATION"));
    }
}
