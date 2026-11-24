using Microsoft.AspNetCore.Http;

namespace HostingPanel.Api;

/// <summary>Handles compatibility routes that do not have a typed provider contract.</summary>
public interface IApiCompatibilityHandler
{
    Task ForwardAsync(HttpContext context);
}
