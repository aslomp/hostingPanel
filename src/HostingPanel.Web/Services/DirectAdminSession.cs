using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace HostingPanel.Web.Services;

// Scoped to an interactive Blazor Server circuit. Credentials are never persisted in a cookie,
// localStorage, appsettings or repository. Use HTTPS and a restricted DirectAdmin login key.
public sealed class DirectAdminSession(IHttpClientFactory clients)
{
    private string? _username;
    private string? _secret;
    public bool Connected => _secret is not null;
    public string Username => _username ?? "";

    public void SetCredentials(string username, string loginKey)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(loginKey))
            throw new ArgumentException("Username and login key are required.");
        _username = username.Trim();
        _secret = loginKey;
    }

    public void Disconnect()
    {
        _username = null;
        _secret = null;
    }

    public async Task<(int Status, string Body)> GetRawAsync(string endpoint, CancellationToken ct = default)
    {
        if (!Connected) throw new InvalidOperationException("Connect to DirectAdmin first.");
        if (!(endpoint.StartsWith("CMD_API_", StringComparison.Ordinal)
              || endpoint.StartsWith("api/", StringComparison.Ordinal)))
            throw new ArgumentException("Only DirectAdmin API endpoints are supported.", nameof(endpoint));

        var client = clients.CreateClient("hosting-api");
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        var pair = Convert.ToBase64String(Encoding.UTF8.GetBytes(_username + ":" + _secret));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", pair);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        return ((int)response.StatusCode, body);
    }

    public async Task<IReadOnlyList<string>> GetUsersAsync(CancellationToken ct = default)
    {
        var (status, body) = await GetRawAsync("CMD_API_SHOW_ALL_USERS?json=yes", ct);
        if (status is < 200 or >= 300)
            throw new HttpRequestException($"DirectAdmin returned HTTP {status}.");
        using var json = JsonDocument.Parse(body);
        if (json.RootElement.ValueKind != JsonValueKind.Array)
            throw new FormatException("Unexpected SHOW_ALL_USERS JSON response.");
        return json.RootElement.EnumerateArray().Select(x => x.GetString() ?? "").ToList();
    }
}
