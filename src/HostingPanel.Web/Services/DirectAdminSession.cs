using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using HostingPanel.Web.Realtime;

namespace HostingPanel.Web.Services;

// Credentials are held only for an interactive Blazor Server circuit.
// Never persist or log login keys. Always deploy behind HTTPS.
public sealed class DirectAdminSession(IHttpClientFactory clients, TicketChannelRegistry ticketChannels) : IDisposable
{
    private string? _username;
    private string? _loginKey;
    private string? _impersonatedUsername;
    private string? _ticketChannel;
    public string? TicketChannel => _ticketChannel;
    public event Action? Changed;
    public event Action? TicketInboxChanged;
    public void NotifyTicketInboxChanged() => TicketInboxChanged?.Invoke();
    private void NotifyChanged() => Changed?.Invoke();

    public bool Connected => _loginKey is not null;
    public string Username => _impersonatedUsername ?? _username ?? "";
    public string OriginalUsername => _username ?? "";
    public bool IsImpersonating => _impersonatedUsername is not null;

    public void SetCredentials(string username, string loginKey)
    {
        if (string.IsNullOrWhiteSpace(loginKey) || !IsValidAccountName(username))
            throw new ArgumentException("A valid username and login key are required.");
        ticketChannels.Revoke(_ticketChannel);
        _ticketChannel = ticketChannels.Register();
        _username = username.Trim();
        _loginKey = loginKey;
        _impersonatedUsername = null;
        NotifyChanged();
    }

    public void Disconnect()
    {
        ticketChannels.Revoke(_ticketChannel);
        _ticketChannel = null;
        _username = null;
        _loginKey = null;
        _impersonatedUsername = null;
        NotifyChanged();
    }

    public void Dispose()
    {
        ticketChannels.Revoke(_ticketChannel);
        _ticketChannel = null;
        _username = null;
        _loginKey = null;
        _impersonatedUsername = null;
    }

    public Task<(int Status, string Body)> AuthenticateBackendAsync(CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, "hosting/session", null, ct);

    public Task<(int Status, string Body)> GetBackendSettingsAsync(CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, "hosting/backend-settings", null, ct);

    public async Task<(int Status, string Body)> SaveBackendSettingsAsync(string json, CancellationToken ct = default)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await SendAsync(HttpMethod.Put, "hosting/backend-settings", content, ct);
    }

    public async Task<(int Status, string Body)> GetRawAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        if (!Connected) throw new InvalidOperationException("Maak eerst verbinding met DirectAdmin.");
        if (!IsAllowedEndpoint(endpoint))
            throw new ArgumentException("Only relative DirectAdmin API paths are allowed.", nameof(endpoint));

        return await SendAsync(HttpMethod.Get, endpoint, null, cancellationToken);
    }

    /// <summary>
    /// Call a modern DirectAdmin operation through HostingPanel.Api with the current
    /// effective identity. Authorization remains entirely with the backend.
    /// </summary>
    public async Task<(int Status, string Body)> RequestApiAsync(
        HttpMethod method, string endpoint, string? json = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsAllowedEndpoint(endpoint))
            throw new ArgumentException("Only relative modern DirectAdmin API paths are allowed.", nameof(endpoint));
        if (method != HttpMethod.Get && method != HttpMethod.Post &&
            method != HttpMethod.Put && method != HttpMethod.Patch &&
            method != HttpMethod.Delete)
            throw new ArgumentException("Unsupported HTTP method.", nameof(method));
        if (json is not null)
        {
            using var parsed = JsonDocument.Parse(json);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            return await SendAsync(method, endpoint, content, cancellationToken);
        }
        return await SendAsync(method, endpoint, null, cancellationToken);
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
                401 or 403 => "DirectAdmin does not permit signing in as this user.",
                501 => "The DirectAdmin server does not support the required session validation.",
                _ => $"Impersonation could not be validated (HTTP {status})."
            };
            throw new UnauthorizedAccessException(error);
        }

        using var response = JsonDocument.Parse(body);
        if (response.RootElement.ValueKind != JsonValueKind.Object ||
            !response.RootElement.TryGetProperty("allowed", out var allowed) ||
            allowed.ValueKind != JsonValueKind.True ||
            !response.RootElement.TryGetProperty("effectiveUsername", out var actual) ||
            !username.Equals(actual.GetString(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The target identity could not be confirmed.");
        ticketChannels.Revoke(_ticketChannel);
        _ticketChannel = ticketChannels.Register();
        _impersonatedUsername = username;
        NotifyChanged();
    }

    public void EndImpersonation()
    {
        ticketChannels.Revoke(_ticketChannel);
        _ticketChannel = ticketChannels.Register();
        _impersonatedUsername = null;
        NotifyChanged();
    }

    private static bool IsValidAccountName(string? username) =>
        !string.IsNullOrWhiteSpace(username) && username.Length <= 64 &&
        username.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    public async Task<(int Status, string Body)> CreateHostingAccountAsync(
        string kind, string username, string email, string password,
        string domain, string package, string ip, CancellationToken ct = default)
    {
        if (kind is not ("user" or "reseller"))
            throw new ArgumentException("Invalid account kind.", nameof(kind));
        using var body = new StringContent(JsonSerializer.Serialize(new {
            kind, username, email, password, domain, package, ip
        }), Encoding.UTF8, "application/json");
        return await SendAsync(HttpMethod.Post, kind == "reseller" ?
            "hosting/resellers" : "hosting/users", body, ct);
    }

    public async Task<(int Status, string Body)> DeleteHostingAccountAsync(
        string kind, string username, string confirmation, CancellationToken ct = default)
    {
        if (kind is not ("user" or "reseller") || !IsValidAccountName(username))
            throw new ArgumentException("Invalid account.");
        using var body = new StringContent(
            JsonSerializer.Serialize(new { confirmation }), Encoding.UTF8, "application/json");
        return await SendAsync(HttpMethod.Delete,
            (kind == "reseller" ? "hosting/resellers/" : "hosting/users/") +
            Uri.EscapeDataString(username), body, ct);
    }

    public async Task<(int Status, string Body)> SetUserPackageAsync(
        string username, string package, CancellationToken ct = default)
    {
        if (!IsValidAccountName(username)) throw new ArgumentException("Invalid account.");
        using var body = new StringContent(JsonSerializer.Serialize(new { package }),
            Encoding.UTF8, "application/json");
        return await SendAsync(HttpMethod.Put,
            "hosting/users/" + Uri.EscapeDataString(username) + "/package", body, ct);
    }

    public async Task<(int Status, string Body)> SetAccountLimitAsync(
        string kind, string username, string field, string value, CancellationToken ct = default)
    {
        if (kind is not ("user" or "reseller") || !IsValidAccountName(username))
            throw new ArgumentException("Invalid account.");
        using var body = new StringContent(JsonSerializer.Serialize(new { field, value }),
            Encoding.UTF8, "application/json");
        return await SendAsync(HttpMethod.Put,
            "hosting/" + (kind == "reseller" ? "resellers/" : "users/") +
            Uri.EscapeDataString(username) + "/limit", body, ct);
    }

    public async Task<(int Status, string Body)> SetAccountSuspensionAsync(
        string username, bool suspended, CancellationToken ct = default)
    {
        if (!IsValidAccountName(username)) throw new ArgumentException("Invalid account.");
        using var body = new StringContent(JsonSerializer.Serialize(new { suspended }),
            Encoding.UTF8, "application/json");
        return await SendAsync(HttpMethod.Put,
            "hosting/accounts/" + Uri.EscapeDataString(username) + "/suspension", body, ct);
    }

    public async Task<(int Status, string Body)> CreateDnsZoneAsync(
        string domain, string ip, string ns1, string ns2, CancellationToken ct = default)
    {
        using var body = new StringContent(
            JsonSerializer.Serialize(new { domain, ip, ns1, ns2 }),
            Encoding.UTF8, "application/json");
        return await SendAsync(HttpMethod.Post, "hosting/dns/zones", body, ct);
    }

    public async Task<(int Status, string Body)> DeleteDnsZoneAsync(
        string domain, string confirmation, CancellationToken ct = default)
    {
        using var body = new StringContent(
            JsonSerializer.Serialize(new { confirmation }), Encoding.UTF8, "application/json");
        return await SendAsync(HttpMethod.Delete, "hosting/dns/zones/" +
            Uri.EscapeDataString(domain), body, ct);
    }

    public Task<(int Status, string Body)> GetAccessibleDatabasesAsync(
        CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, "hosting/databases/accessible", null, ct);

    public Task<(int Status, string Body)> GetDatabasesAsync(
        CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, "hosting/databases", null, ct);

    public Task<(int Status, string Body)> GetDatabaseUsersAsync(
        string database, CancellationToken ct = default)
    {
        if (!ValidDatabaseName(database))
            throw new ArgumentException("Invalid database.", nameof(database));
        return SendAsync(HttpMethod.Get,
            "hosting/databases/" + Uri.EscapeDataString(database) + "/users", null, ct);
    }

    public async Task<(int Status, string Body)> CreateDatabaseAsync(
        string database, string dbUser, string password, CancellationToken ct = default)
    {
        if (!ValidDatabaseName(database) || !ValidDatabaseName(dbUser) ||
            string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("Invalid database creation request.");
        using var body = new StringContent(
            JsonSerializer.Serialize(new { database, dbUser, password }),
            Encoding.UTF8, "application/json");
        return await SendAsync(HttpMethod.Post, "hosting/databases", body, ct);
    }

    public async Task<(int Status, string Body)> DeleteDatabaseAsync(
        string database, string confirmation, CancellationToken ct = default)
    {
        if (!ValidDatabaseName(database) || database != confirmation)
            throw new ArgumentException("The confirmation must match the database name.");
        using var body = new StringContent(JsonSerializer.Serialize(new { confirmation }),
            Encoding.UTF8, "application/json");
        return await SendAsync(HttpMethod.Delete,
            "hosting/databases/" + Uri.EscapeDataString(database), body, ct);
    }

    private static bool ValidDatabaseName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 64 &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    public Task<(int Status, string Body)> GetAccessibleResellerAccountsAsync(
        CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, "hosting/resellers/accessible", null, ct);

    public Task<(int Status, string Body)> GetAccessibleAccountsAsync(
        CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, "hosting/accounts", null, cancellationToken);

    public Task<(int Status, string Body)> GetAccessibleDomainsAsync(
        CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, "hosting/domains/accessible", null, cancellationToken);

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

    public Task<(int Status, string Body)> GetAccessibleDnsZonesAsync(
        CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, "hosting/dns/zones/accessible", null, cancellationToken);

    public Task<(int Status, string Body)> GetAdminDnsZonesAsync(int page = 1,
        CancellationToken cancellationToken = default)
    {
        if (page is < 1 or > 10000)
            throw new ArgumentOutOfRangeException(nameof(page));
        return SendAsync(HttpMethod.Get, "hosting/dns/zones?page=" +
            page.ToString(System.Globalization.CultureInfo.InvariantCulture),
            null, cancellationToken);
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
            throw new UnauthorizedAccessException("DirectAdmin rejected the sign-in.");
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
