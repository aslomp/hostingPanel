namespace HostingPanel.Web.Services;

// Settings and network endpoints retain their existing wire format.
public sealed class SettingsOperations(PanelHttpTransport transport)
{
    public Task<(int Status, string Body)> GetBackendSettingsAsync(CancellationToken ct = default) =>
        transport.SendAsync(HttpMethod.Get, "hosting/backend-settings", cancellationToken: ct);

    public Task<(int Status, string Body)> GetNetworkInventoryAsync(CancellationToken ct = default) =>
        transport.SendAsync(HttpMethod.Get, "hosting/network/inventory", cancellationToken: ct);

    public Task<(int Status, string Body)> SaveBackendSettingsAsync(string json, CancellationToken ct = default) =>
        transport.SendJsonAsync(HttpMethod.Put, "hosting/backend-settings", json, ct);

    public Task<(int Status, string Body)> GetProfileNameserversAsync(CancellationToken ct = default) =>
        transport.SendAsync(HttpMethod.Get, "hosting/profile/nameservers", cancellationToken: ct);

    public Task<(int Status, string Body)> SaveProfileNameserversAsync(string json, CancellationToken ct = default) =>
        transport.SendJsonAsync(HttpMethod.Put, "hosting/profile/nameservers", json, ct);
}
