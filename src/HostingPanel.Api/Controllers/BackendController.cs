using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc;
namespace HostingPanel.Api;

public abstract class BackendController : ControllerBase
{
    protected static bool TryGetBasic(HttpContext context, out AuthenticationHeaderValue authentication)
    {
        authentication = null!;
        if (!AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization.ToString(), out var value)
            || !value.Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(value.Parameter))
        {
            context.Response.Headers.WWWAuthenticate = "Basic realm=\"HostingPanel API\"";
            return false;
        }
        authentication = value;
        return true;
    }
}
