using System.Text;

namespace HostingPanel.Web.Services;

// Settings and network endpoints retain their existing wire format.
public sealed class SettingsOperations(IHttpClientFactory clients, PanelSession session)
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
    private async Task<(int Status, string Body)> SendAsync(HttpMethod method, string endpoint, HttpContent? content, CancellationToken ct)
    {
        var http = clients.CreateClient("hosting-api");
        using var request = new HttpRequestMessage(method, endpoint) { Content = content };
        request.Headers.Authorization = session.EffectiveCredentials();
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }
}
