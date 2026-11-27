using HostingPanel.Providers.PowerDNS;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HostingPanel.Api.Adapters;
using HostingPanel.Api.Persistence;
using HostingPanel.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace HostingPanel.Api;

public sealed class KubernetesCompatibilityHandler(KubernetesDatabaseCompatibility? database = null, KubernetesDnsAdminCompatibility? dns = null) : IApiCompatibilityHandler
{
    public Task ForwardAsync(HttpContext context)
    {
        if (dns is not null && string.Equals(context.Request.Path.Value, "/CMD_API_DNS_ADMIN", StringComparison.OrdinalIgnoreCase))
            return dns.ForwardAsync(context);
        if (database is not null &&
            (context.Request.Path.StartsWithSegments("/api/db-show") ||
             context.Request.Path.StartsWithSegments("/api/db-manage") ||
             context.Request.Path.StartsWithSegments("/api/db-monitor")))
            return database.ForwardAsync(context);
        context.Response.StatusCode = StatusCodes.Status501NotImplemented;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsJsonAsync(new { type = "KUBERNETES_OPERATION_NOT_IMPLEMENTED" });
    }
}
