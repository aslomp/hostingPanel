using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

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

        return await SendAsync(HttpMethod.Get, endpoint, null, cancellationToken);
    }

    private async Task<(int Status, string Body)> SendAsync(HttpMethod method, string endpoint,
        HttpContent? content, CancellationToken cancellationToken)
    {
        if (!Connected) throw new InvalidOperationException("Maak eerst verbinding met DirectAdmin.");
        var http = clients.CreateClient("hosting-api");
        using var request = new HttpRequestMessage(method, endpoint) { Content = content };
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(_username + ":" + _loginKey));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
    }

    public async Task<(int Status, string Body)> GetDomainsAsync(string? username = null,
        CancellationToken cancellationToken = default)
    {
        if (username is not null && (username.Length is < 1 or > 64 ||
            !username.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')))
            throw new ArgumentException("Ongeldige gebruiker.", nameof(username));
        return await SendAsync(HttpMethod.Get,
            "hosting/domains" + (username is null ? "" : "?user=" + Uri.EscapeDataString(username)),
            null, cancellationToken);
    }

    public async Task<(int Status, string Body)> PreviewDomainAsync(string domain, bool php,
        bool ssl, bool cgi, CancellationToken cancellationToken = default)
    {
        using var content = new StringContent(
            JsonSerializer.Serialize(new { domain, php, ssl, cgi }), Encoding.UTF8, "application/json");
        return await SendAsync(HttpMethod.Post, "hosting/domains/preview", content, cancellationToken);
    }

    public async Task<(int Status, string Body)> ExecuteTestDomainAsync(
        string domain, string action, string confirmation, bool php, bool ssl, bool cgi,
        CancellationToken cancellationToken = default)
    {
        if (action is not ("create" or "modify" or "delete"))
            throw new ArgumentException("Unknown domain operation.", nameof(action));
        using var content = new StringContent(
            JsonSerializer.Serialize(new { domain, action, confirmation, php, ssl, cgi }),
            Encoding.UTF8, "application/json");
        return await SendAsync(HttpMethod.Post, "hosting/domains/execute", content, cancellationToken);
    }

    public async Task<(int Status, string Body)> GetDomainDetailsAsync(
        string domain, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(domain) || domain.Contains('/') || domain.Contains('\\')
            || domain.Contains('?') || domain.Any(char.IsControl))
            throw new ArgumentException("Invalid domain.", nameof(domain));
        return await SendAsync(HttpMethod.Get,
            "hosting/domains/" + Uri.EscapeDataString(domain), null, cancellationToken);
    }

    public async Task<(int Status, string Body)> SaveDomainSettingAsync(
        string domain, string setting, string value, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(domain) || domain.Contains('/') || domain.Contains('\\')
            || domain.Contains('?') || domain.Any(char.IsControl))
            throw new ArgumentException("Invalid domain.", nameof(domain));
        using var content = new StringContent(
            JsonSerializer.Serialize(new { setting, value }), Encoding.UTF8, "application/json");
        return await SendAsync(HttpMethod.Put,
            "hosting/domains/" + Uri.EscapeDataString(domain) + "/settings", content, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> GetUsersAsync(CancellationToken cancellationToken = default)
    {
        var (status, body) = await GetRawAsync("api/search/users?limit=1000", cancellationToken);
        if (status == 401 || status == 403)
            throw new UnauthorizedAccessException("DirectAdmin heeft de aanmelding geweigerd.");
        if (status is < 200 or >= 300)
            throw new HttpRequestException($"DirectAdmin reageert met HTTP {status}.");
        return DirectAdminUsersParser.Parse(body);
    }

    public async Task<(int Status, string Body)> GetUserDetailAsync(
        string username, string kind, CancellationToken cancellationToken = default)
    {
        if (kind is not ("config" or "usage"))
            throw new ArgumentException("Invalid user detail type.", nameof(kind));
        if (username.Length is < 1 or > 64 ||
            !username.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
            throw new ArgumentException("Invalid username.", nameof(username));
        return await GetRawAsync($"api/users/{Uri.EscapeDataString(username)}/{kind}", cancellationToken);
    }

    public Task<(int Status, string Body)> GetAccountOverviewAsync(string username,
        CancellationToken cancellationToken = default)
    {
        if (username.Length is < 1 or > 64 ||
            !username.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
            throw new ArgumentException("Invalid username.", nameof(username));
        return SendAsync(HttpMethod.Get,
            "hosting/users/" + Uri.EscapeDataString(username) + "/overview",
            null, cancellationToken);
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
        return path.StartsWith("api/", StringComparison.Ordinal);
    }
}
