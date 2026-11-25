using System.Text;

namespace HostingPanel.Web.Services;

// Shared same-host HTTP transport for transitional UI operations.
// Authorization is always derived from the active server-side session, never from caller input.
public sealed class PanelHttpTransport(IHttpClientFactory clients, PanelSession session)
{
    public async Task<(int Status, string Body)> SendJsonAsync(
        HttpMethod method, string endpoint, string json, CancellationToken cancellationToken = default)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await SendAsync(method, endpoint, content, cancellationToken);
    }

    public async Task<(int Status, string Body)> SendAsync(
        HttpMethod method, string endpoint, HttpContent? content = null,
        CancellationToken cancellationToken = default)
    {
        if (!session.Connected) throw new InvalidOperationException("Sign in first.");
        var http = clients.CreateClient("hosting-api");
        using var request = new HttpRequestMessage(method, endpoint) { Content = content };
        request.Headers.Authorization = session.EffectiveCredentials();
        using var response = await http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
    }
}
