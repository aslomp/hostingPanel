using System.Text;

namespace HostingPanel.Web.Services;

// Settings and network endpoints retain their existing wire format.
public sealed class SettingsOperations(PanelHttpTransport transport)
{
    public Task<(int Status, string Body)> GetBackendSettingsAsync(CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, "hosting/backend-settings", null, ct);
    public Task<(int Status, string Body)> GetNetworkInventoryAsync(CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, "hosting/network/inventory", null, ct);
    public async Task<(int Status, string Body)> SaveBackendSettingsAsync(string json, CancellationToken ct = default)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await SendAsync(HttpMethod.Put, "hosting/backend-settings", content, ct);
    }
    public Task<(int Status, string Body)> GetProfileNameserversAsync(CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, "hosting/profile/nameservers", null, ct);
    public async Task<(int Status, string Body)> SaveProfileNameserversAsync(string json, CancellationToken ct = default)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await SendAsync(HttpMethod.Put, "hosting/profile/nameservers", content, ct);
    }
    private async Task<(int Status, string Body)> SendAsync(HttpMethod method, string endpoint, HttpContent? content, CancellationToken ct)
    {
        return await transport.SendAsync(method, endpoint, content, ct);
    }
}
