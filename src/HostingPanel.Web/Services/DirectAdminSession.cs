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
    private string? _impersonatedUsername;

    public bool Connected => _loginKey is not null;
    public string Username => _impersonatedUsername ?? _username ?? "";
    public string OriginalUsername => _username ?? "";
    public bool IsImpersonating => _impersonatedUsername is not null;

    public void SetCredentials(string username, string loginKey)
    {
        if (string.IsNullOrWhiteSpace(loginKey) || !IsValidAccountName(username))
            throw new ArgumentException("Geldige gebruikersnaam en login key zijn verplicht.");
        _username = username.Trim();
        _loginKey = loginKey;
        _impersonatedUsername = null;
    }

    public void Disconnect()
    {
        _username = null;
        _loginKey = null;
        _impersonatedUsername = null;
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
        var authenticatedUsername = _impersonatedUsername is null
            ? _username : _username + "|" + _impersonatedUsername;
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(authenticatedUsername + ":" + _loginKey));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
    }

    /// <summary>
    /// Switch the active identity only after HostingPanel.Api has verified the
    /// original administrator and the delegated identity with DirectAdmin.
    /// The original login key stays private inside this Blazor circuit.
    /// </summary>
    public async Task StartImpersonationAsync(string username, CancellationToken ct = default)
    {
        if (!Connected) throw new InvalidOperationException("Maak eerst verbinding.");
        if (IsImpersonating)
            throw new InvalidOperationException("Keer eerst terug naar je eigen sessie.");
        if (!IsValidAccountName(username) || username.Equals(_username, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Ongeldige doelgebruiker.", nameof(username));

        var (status, body) = await SendAsync(HttpMethod.Get,
            "hosting/impersonation/check?user=" + Uri.EscapeDataString(username), null, ct);
        if (status != 200)
        {
            string error = status switch
            {
                401 or 403 => "DirectAdmin staat aanmelden als deze gebruiker niet toe.",
                501 => "De DirectAdmin-server ondersteunt de vereiste sessievalidatie niet.",
                _ => $"Impersonation kon niet worden gevalideerd (HTTP {status})."
            };
            throw new UnauthorizedAccessException(error);
        }

        using var response = JsonDocument.Parse(body);
        if (response.RootElement.ValueKind != JsonValueKind.Object ||
            !response.RootElement.TryGetProperty("allowed", out var allowed) ||
            allowed.ValueKind != JsonValueKind.True ||
            !response.RootElement.TryGetProperty("effectiveUsername", out var actual) ||
            !username.Equals(actual.GetString(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("De doelidentiteit kon niet worden bevestigd.");
        _impersonatedUsername = username;
    }

    public void EndImpersonation()
    {
        _impersonatedUsername = null;
    }

    private static bool IsValidAccountName(string? username) =>
        !string.IsNullOrWhiteSpace(username) && username.Length <= 64 &&
        username.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

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

    public Task<(int Status, string Body)> GetDnsZoneAsync(string domain,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(domain) || domain.Contains('/') || domain.Contains('\\')
            || domain.Contains('?') || domain.Any(char.IsControl))
            throw new ArgumentException("Invalid domain.", nameof(domain));
        return SendAsync(HttpMethod.Get,
            "hosting/domains/" + Uri.EscapeDataString(domain) + "/dns",
            null, cancellationToken);
    }

    public async Task<(int Status, string Body)> ChangeDnsRecordAsync(string domain, string action,
        string type, string name, string value, string? ttl,
        string? previousType, string? previousName, string? previousValue,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(domain) || domain.Contains('/') || domain.Contains('\\')
            || domain.Contains('?') || domain.Any(char.IsControl))
            throw new ArgumentException("Invalid domain.", nameof(domain));
        var previous = previousType is null ? null : new {
            type = previousType, name = previousName, value = previousValue
        };
        using var body = new StringContent(JsonSerializer.Serialize(new {
            action, record = new { type, name, value, ttl }, previous
        }), Encoding.UTF8, "application/json");
        return await SendAsync(HttpMethod.Post, "hosting/domains/" +
            Uri.EscapeDataString(domain) + "/dns", body, cancellationToken);
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
