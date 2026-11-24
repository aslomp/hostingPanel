using System.Text;
using System.Text.Json;

namespace HostingPanel.Web.Services;

// Transport for API compatibility endpoints without a typed provider contract.
public sealed class CompatibilityApiClient(PanelHttpTransport transport, PanelSession session)
{
    public async Task<(int Status, string Body)> GetRawAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        if (!session.Connected) throw new InvalidOperationException("Sign in first.");
        if (!IsAllowedEndpoint(endpoint))
            throw new ArgumentException("Only relative DirectAdmin API paths are allowed.", nameof(endpoint));

        return await transport.SendAsync(HttpMethod.Get, endpoint, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Call a modern DirectAdmin operation through HostingPanel.Api with the current
    /// effective identity. Authorization remains entirely with the backend.
    /// </summary>
    public async Task<(int Status, string Body)> RequestApiAsync(
        HttpMethod method, string endpoint, string? json = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsAllowedEndpoint(endpoint))
            throw new ArgumentException("Only relative modern DirectAdmin API paths are allowed.", nameof(endpoint));
        if (method != HttpMethod.Get && method != HttpMethod.Post &&
            method != HttpMethod.Put && method != HttpMethod.Patch &&
            method != HttpMethod.Delete)
            throw new ArgumentException("Unsupported HTTP method.", nameof(method));
        if (json is not null)
        {
            using var parsed = JsonDocument.Parse(json);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            return await transport.SendAsync(method, endpoint, content, cancellationToken);
        }
        return await transport.SendAsync(method, endpoint, cancellationToken: cancellationToken);
    }

    public static bool IsAllowedEndpoint(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || endpoint[0] == '/'
            || endpoint.Contains("..", StringComparison.Ordinal)
            || endpoint.Contains('\\') || endpoint.Contains('#')
            || endpoint.Any(char.IsControl))
            return false;

        var path = endpoint.Split('?', 2)[0];
        return path.StartsWith("api/", StringComparison.Ordinal)
            || path == "hosting/tickets/inbox"
            || path.StartsWith("hosting/tickets/", StringComparison.Ordinal);
    }
}
