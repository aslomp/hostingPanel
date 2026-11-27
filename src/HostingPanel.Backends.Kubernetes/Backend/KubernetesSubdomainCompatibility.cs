using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.Primitives;

namespace HostingPanel.Api;

public sealed class KubernetesSubdomainCompatibility(ISubdomains subdomains)
{
    public async Task ForwardAsync(HttpContext context)
    {
        var ct = context.RequestAborted;
        var request = context.Request;
        if (!AuthenticationHeaderValue.TryParse(request.Headers.Authorization.ToString(), out var auth) ||
            !auth.Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.Headers.WWWAuthenticate = "Basic realm=\"HostingPanel API\"";
            await Error(context, 401, "UNAUTHORIZED"); return;
        }
        var fields = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase);
        bool Collect(IEnumerable<KeyValuePair<string, StringValues>> values)
        {
            foreach (var pair in values)
                if (pair.Value.Count != 1 || !fields.TryAdd(pair.Key, pair.Value)) return false;
            return true;
        }
        if (!Collect(request.Query)) { await Error(context, 400, "AMBIGUOUS_FORM"); return; }
        if (HttpMethods.IsPost(request.Method) && (request.ContentLength is > 0 ||
            request.Headers.ContainsKey("Transfer-Encoding") || request.HasFormContentType))
        {
            try
            {
                if (!request.HasFormContentType || !Collect(await request.ReadFormAsync(ct)))
                { await Error(context, 400, "INVALID_OR_AMBIGUOUS_FORM"); return; }
            }
            catch (Exception ex) when (ex is InvalidDataException or BadHttpRequestException)
            { await Error(context, 400, "INVALID_FORM"); return; }
        }
        string Read(string key) => fields.TryGetValue(key, out var value) ? value.ToString() : "";
        var domain = Read("domain").ToLowerInvariant();
        var action = Read("action");
        if (!SubdomainNames.ValidDomain(domain)) { await Error(context, 400, "INVALID_DOMAIN"); return; }
        if (action == "" && fields.Keys.All(k => k.Equals("domain", StringComparison.OrdinalIgnoreCase)))
        {
            var result = await subdomains.ListAsync(domain, auth, ct);
            if (result.Status == 200 && result.Names is not null)
                await Write(context, 200, result.Names.Select(x => new KeyValuePair<string, string>("list[]", x)));
            else await Error(context, result.Status, result.Error ?? "SUBDOMAIN_READ_FAILED");
            return;
        }
        SubdomainMutation mutation;
        if (action == "create" && fields.Keys.All(k => new[] { "action", "domain", "subdomain" }.Contains(k, StringComparer.OrdinalIgnoreCase)))
            mutation = await subdomains.CreateAsync(domain, Read("subdomain"), auth, ct);
        else if (action == "delete" && fields.Keys.All(k => new[] { "action", "domain", "select0", "contents" }.Contains(k, StringComparer.OrdinalIgnoreCase)))
        {
            // The current lifecycle deletes files. Never silently interpret
            // contents=no (or omitted) as permission to delete customer data.
            if (Read("contents") != "yes") { await Error(context, 501, "RETAIN_CONTENTS_NOT_SUPPORTED"); return; }
            var label = Read("select0").ToLowerInvariant();
            mutation = await subdomains.DeleteAsync(domain, label, label + "." + domain, auth, ct);
        }
        else { await Error(context, 501, "UNSUPPORTED_SUBDOMAIN_OPERATION_OR_PARAMETER"); return; }
        if (mutation.Error is null && mutation.Status is >= 200 and < 300)
            await Write(context, 200, [new("error", "0"), new("text", "Success"),
                new("details", mutation.Status == 202 ? "Provisioning pending" : "")]);
        else await Error(context, mutation.Status, mutation.Error ?? "SUBDOMAIN_MUTATION_FAILED");
    }

    private static Task Error(HttpContext context, int status, string code)
    {
        if (status == 401) context.Response.Headers.WWWAuthenticate = "Basic realm=\"HostingPanel API\"";
        return Write(context, status is 401 or 403 or 501 or 502 or 503 or 504 ? status : 200,
            [new("error", "1"), new("text", code), new("details", code)]);
    }

    private static Task Write(HttpContext context, int status, IEnumerable<KeyValuePair<string, string>> fields)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/x-www-form-urlencoded; charset=utf-8";
        return context.Response.WriteAsync(new QueryBuilder(fields).ToQueryString().Value?.TrimStart('?') ?? "", context.RequestAborted);
    }
}
