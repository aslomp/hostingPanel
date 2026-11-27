using System.Net.Http.Headers;
using HostingPanel.Api.Adapters;
using HostingPanel.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.Primitives;

namespace HostingPanel.Api;

/// <summary>Translate documented legacy DNS administration commands into existing DNS desired-state operations.</summary>
public sealed class KubernetesDnsAdminCompatibility(KubernetesIdentityService identity, IDNSProvider dns)
{
    public async Task ForwardAsync(HttpContext context)
    {
        var ct = context.RequestAborted;
        var request = context.Request;
        if (!AuthenticationHeaderValue.TryParse(request.Headers.Authorization.ToString(), out var auth)
            || await identity.AuthenticateAsync(auth, ct) is not { } principal)
        {
            context.Response.Headers.WWWAuthenticate = "Basic realm=\"HostingPanel API\"";
            await Error(context, "UNAUTHORIZED", 401);
            return;
        }
        // CMD_API_DNS_ADMIN is admin-only, even for zones owned by a reseller/user.
        if (principal.Kind != "admin")
        {
            await Error(context, "ACCESS_DENIED", 403);
            return;
        }

        var fields = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase);
        bool Collect(IEnumerable<KeyValuePair<string, StringValues>> values)
        {
            foreach (var pair in values)
                if (pair.Value.Count != 1 || !fields.TryAdd(pair.Key, pair.Value)) return false;
            return true;
        }
        if (!Collect(request.Query))
        {
            await Error(context, "AMBIGUOUS_FORM", 400);
            return;
        }
        if (HttpMethods.IsPost(request.Method) && (request.ContentLength is > 0 || request.Headers.ContainsKey("Transfer-Encoding") || request.HasFormContentType))
        {
            try
            {
                if (!request.HasFormContentType || !Collect(await request.ReadFormAsync(ct)))
                {
                    await Error(context, "INVALID_OR_AMBIGUOUS_FORM", 400);
                    return;
                }
            }
            catch (InvalidDataException)
            {
                await Error(context, "INVALID_FORM", 400);
                return;
            }
            catch (BadHttpRequestException)
            {
                await Error(context, "INVALID_FORM", 400);
                return;
            }
        }
        string Read(string key) => fields.TryGetValue(key, out var value) ? value.ToString() : "";
        var action = Read("action");
        var domain = Read("domain").TrimEnd('.').ToLowerInvariant();
        if (!KubernetesInputValidation.ValidDomain(domain))
        {
            await Error(context, "INVALID_DOMAIN", 400);
            return;
        }
        var allowed = action switch
        {
            "exists" => new[] { "action", "domain" },
            "create" => new[] { "action", "domain", "ip", "ns1", "ns2" },
            "add" => new[] { "action", "domain", "type", "name", "value", "ttl" },
            _ => Array.Empty<string>()
        };
        if (allowed.Length == 0 || fields.Keys.Any(k => !allowed.Contains(k, StringComparer.OrdinalIgnoreCase)))
        {
            await Error(context, "UNSUPPORTED_DNS_OPERATION_OR_PARAMETER", 501);
            return;
        }

        if (action == "exists")
        {
            var result = await dns.ReadAsync(domain, auth, ct);
            if (result.Status == 404 && result.Error == "ZONE_NOT_FOUND")
                await Write(context, 200, ("exists", "0"));
            else if (result.Status == 200 && result.Records is not null)
                await Write(context, 200, ("exists", "1"));
            else
                await Error(context, result.Error ?? "DNS_READ_FAILED", result.Status);
            return;
        }
        if (action == "create")
        {
            var ns1 = Read("ns1").TrimEnd('.');
            var ns2 = Read("ns2").TrimEnd('.');
            if (string.Equals(ns1, ns2, StringComparison.OrdinalIgnoreCase))
            {
                await Error(context, "INVALID_DNS_ZONE", 400);
                return;
            }
            await Outcome(context, await dns.CreateZoneAsync(new DnsZoneCreate(domain, Read("ip"), ns1, ns2), auth, ct));
            return;
        }
        var record = new DnsRecord(Read("type").ToUpperInvariant(), Read("name"), Read("value"),
            fields.ContainsKey("ttl") ? Read("ttl") : null);
        await Outcome(context, await dns.ChangeAsync(domain, new DnsChange("add", record), auth, ct));
    }

    private static Task Outcome(HttpContext context, DnsChangeResult result) =>
        result.Error is null && (result.Executed || result.ProvisioningPending)
            ? Write(context, 200, ("error", "0"), ("text", "Success"),
                ("details", result.ProvisioningPending ? "DNS publication pending" : ""))
            : Error(context, result.Error ?? "DNS_MUTATION_FAILED", result.Status);

    private static Task Error(HttpContext context, string code, int status) =>
        Write(context, status is 401 or 403 or 501 or 502 or 503 or 504 ? status : 200,
            ("error", "1"), ("text", code), ("details", code));

    private static Task Write(HttpContext context, int status, params (string Key, string Value)[] fields)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/x-www-form-urlencoded; charset=utf-8";
        var body = new QueryBuilder(fields.Select(x => new KeyValuePair<string, string>(x.Key, x.Value)))
            .ToQueryString().Value?.TrimStart('?') ?? "";
        return context.Response.WriteAsync(body, context.RequestAborted);
    }
}
