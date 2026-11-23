using System.Net.Http.Headers;
using System.Text;

namespace HostingPanel.Web.Services;

// Credentials are held only for an interactive Blazor Server circuit.
// Never persist or log login keys. Always deploy behind HTTPS.
public sealed class DirectAdminSession(IHttpClientFactory clients)
{
    private string? _username;
    private string? _loginKey;

    public bool Connected => _loginKey is not null;
    public string Username => _username ?? "";

    public void SetCredentials(string username, string loginKey)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(loginKey))
            throw new ArgumentException("Gebruikersnaam en login key zijn verplicht.");
        _username = username.Trim();
        _loginKey = loginKey;
    }

    public void Disconnect()
    {
        _username = null;
        _loginKey = null;
    }

    public async Task<(int Status, string Body)> GetRawAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        if (!Connected) throw new InvalidOperationException("Maak eerst verbinding met DirectAdmin.");
        if (!IsAllowedEndpoint(endpoint))
            throw new ArgumentException("Alleen relatieve DirectAdmin API-paden zijn toegestaan.", nameof(endpoint));

        var http = clients.CreateClient("hosting-api");
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(_username + ":" + _loginKey));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
    }

    public async Task<IReadOnlyList<string>> GetUsersAsync(CancellationToken cancellationToken = default)
    {
        var (status, body) = await GetRawAsync("CMD_API_SHOW_ALL_USERS?json=yes", cancellationToken);
        if (status == 401 || status == 403)
            throw new UnauthorizedAccessException("DirectAdmin heeft de aanmelding geweigerd.");
        if (status is < 200 or >= 300)
            throw new HttpRequestException($"DirectAdmin reageert met HTTP {status}.");
        return DirectAdminUsersParser.Parse(body);
    }

    // Reject absolute URLs, path traversal and cross-endpoint ambiguity at the UI boundary.
    public static bool IsAllowedEndpoint(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || endpoint[0] == '/'
            || endpoint.Contains("..", StringComparison.Ordinal)
            || endpoint.Contains('\\') || endpoint.Contains('#')
            || endpoint.Any(char.IsControl))
            return false;

        var path = endpoint.Split('?', 2)[0];
        return path.StartsWith("CMD_API_", StringComparison.Ordinal)
               || path.StartsWith("api/", StringComparison.Ordinal)
               || path.Equals("static/swagger.json", StringComparison.Ordinal);
    }
}
