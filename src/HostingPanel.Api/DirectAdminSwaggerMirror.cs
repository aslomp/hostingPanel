using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace HostingPanel.Api;

/// <summary>
/// Serves the complete Swagger 2.0 document shipped by the configured DirectAdmin instance.
/// No endpoint names or operation schemas are invented or manually maintained.
/// </summary>
public sealed class DirectAdminSwaggerMirror(IHttpClientFactory clients, IMemoryCache cache)
{
    private const string CacheKey = "directadmin.swagger.v1";

    public async Task<string> GetAsync(CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue<string>(CacheKey, out var existing) && existing is not null)
            return existing;

        // A public DirectAdmin static document, fetched from a single fixed HTTPS upstream.
        var client = clients.CreateClient("directadmin");
        using var request = new HttpRequestMessage(HttpMethod.Get, "static/swagger.json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (json.Length > 20_000_000)
            throw new FormatException("DirectAdmin Swagger document exceeds the configured limit.");

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            (!root.TryGetProperty("swagger", out var version) ||
              version.GetString() != "2.0") ||
            !root.TryGetProperty("paths", out var paths) ||
            paths.ValueKind != JsonValueKind.Object)
            throw new FormatException("DirectAdmin did not return a Swagger 2.0 document.");

        cache.Set(CacheKey, json, TimeSpan.FromMinutes(5));
        return json;
    }
}
