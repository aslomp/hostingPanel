using System.Text.Json;

namespace HostingPanel.Api;

internal static class StalwartDnsProviderRegistration
{
    internal static bool Enabled(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration["Stalwart:DkimDnsServerId"]) ||
        configuration.GetValue("Stalwart:AutoRegisterDnsProvider", true);

    internal static async Task<string> EnsureAsync(HttpClient client, IConfiguration configuration, CancellationToken ct)
    {
        if (configuration["Stalwart:DkimDnsServerId"] is { Length: > 0 } explicitId) return explicitId;
        var endpoint = configuration["PowerDns:BaseUrl"];
        var key = configuration["PowerDns:ApiKey"];
        var serverId = configuration["PowerDns:ServerId"] ?? "localhost";
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("PowerDNS configuration is incomplete.");
        var url = uri.AbsoluteUri.TrimEnd('/');
        const string description = "HostingPanel managed PowerDNS";
        var providers = await StalwartMailProvider.Query(client, "DnsServer", new { },
            new[] { "id", "@type", "description", "url", "serverId" }, ct);
        var matches = providers.Where(x => x.GetProperty("description").GetString() == description).ToArray();
        if (matches.Length > 1) throw new InvalidOperationException("Duplicate HostingPanel DNS providers.");
        if (matches.Length == 1)
        {
            var provider = matches[0];
            if (provider.GetProperty("@type").GetString() != "PowerDns"
                || provider.GetProperty("url").GetString()?.TrimEnd('/') != url
                || provider.GetProperty("serverId").GetString() != serverId)
                throw new InvalidOperationException("HostingPanel DNS provider configuration conflict.");
            return provider.GetProperty("id").GetString() ?? throw new InvalidOperationException("Missing provider identifier.");
        }
        var result = await StalwartMailProvider.Call(client, "x:DnsServer/set", new { create = new { item =
            new Dictionary<string, object> {
                ["@type"] = "PowerDns", ["description"] = description, ["url"] = url,
                ["serverId"] = serverId, ["apiKey"] = new Dictionary<string, string> {
                    ["@type"] = "Value", ["secret"] = key
                }
            } } }, ct);
        return result.GetProperty("created").GetProperty("item").GetProperty("id").GetString()
            ?? throw new InvalidOperationException("DNS provider creation failed.");
    }
}
