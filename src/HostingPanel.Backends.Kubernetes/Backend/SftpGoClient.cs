using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HostingPanel.Api;

// Internal transport only. Authorization and desired-state ownership belong
// to the account capability/reconciler, not to this privileged client.
internal sealed class SftpGoClient(IHttpClientFactory clients, IConfiguration configuration, NfsDirectoryProvisioner storage)
{
    internal sealed record User(
        [property: JsonPropertyName("username")] string Username,
        [property: JsonPropertyName("home_dir")] string HomeDirectory,
        [property: JsonPropertyName("status")] int Status,
        [property: JsonPropertyName("public_keys")] string[] PublicKeys,
        [property: JsonPropertyName("permissions")] Dictionary<string, string[]> Permissions,
        [property: JsonPropertyName("description")] string Description);

    private async Task<HttpClient> ConnectAsync(CancellationToken ct)
    {
        var endpoint = configuration["SftpGo:BaseUrl"];
        var username = configuration["SftpGo:Username"];
        var password = configuration["SftpGo:Password"];
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || uri.AbsolutePath != "/"
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || string.IsNullOrWhiteSpace(username) || username.Contains(':') || string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("SFTPGo management is not configured.");
        var client = clients.CreateClient("sftpgo-management");
        try
        {
            client.BaseAddress = uri;
            using var request = new HttpRequestMessage(HttpMethod.Get, "api/v2/token");
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + password)));
            using var response = await client.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var token = json.RootElement.GetProperty("access_token").GetString();
            if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("Missing SFTPGo token.");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return client;
        }
        catch { client.Dispose(); throw; }
    }

    private static string PathFor(string username)
    {
        if (username.Length is < 1 or > 64 || !char.IsAsciiLetterOrDigit(username[0])
            || !username.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
            throw new ArgumentException("Invalid SFTP account name.", nameof(username));
        return "api/v2/users/" + Uri.EscapeDataString(username);
    }

    private static async Task<User?> ReadAsync(HttpClient client, string username, CancellationToken ct)
    {
        using var response = await client.GetAsync(PathFor(username), ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var user = await response.Content.ReadFromJsonAsync<User>(cancellationToken: ct);
        if (user is null || user.Username != username)
            throw new InvalidOperationException("Unexpected SFTPGo user response.");
        return user;
    }

    internal async Task<User?> GetAsync(string username, CancellationToken ct)
    {
        using var client = await ConnectAsync(ct);
        return await ReadAsync(client, username, ct);
    }

    private static string OwnerMarker(Guid accountId) => "HostingPanel:" + accountId.ToString("D");

    // Activation is explicitly gated by the storage acceptance configuration.
    // Never adopt or overwrite an unrelated upstream account.
    internal async Task EnsureAsync(Guid accountId, string username, string hostingUsername, string relativePath, string publicKey, bool active, CancellationToken ct)
    {
        if (accountId == Guid.Empty) throw new ArgumentException("Missing account identity.", nameof(accountId));
        var path = PathFor(username);
        var (uid, gid) = await storage.ValidateSftpHomeAsync(hostingUsername, relativePath, ct);
        var home = "/home/" + hostingUsername + (relativePath.Length == 0 ? "" : "/" + relativePath);
        var marker = OwnerMarker(accountId);
        using var client = await ConnectAsync(ct);
        var existing = await ReadAsync(client, username, ct);
        if (existing is not null && existing.Description != marker)
            throw new InvalidOperationException("SFTPGo user ownership conflict.");
        var payload = new {
            username, home_dir = home, uid, gid, status = active ? 1 : 0,
            description = marker, public_keys = active ? new[] { publicKey } : Array.Empty<string>(),
            permissions = new Dictionary<string, string[]> { ["/"] = active ? new[] { "list", "download", "upload", "overwrite", "delete", "rename", "create_dirs" } : new[] { "list", "download" } },
            filters = new { denied_login_methods = new[] { "password", "keyboard-interactive", "publickey+password", "publickey+keyboard-interactive" },
                denied_protocols = new[] { "FTP", "DAV", "HTTP" } }
        };
        using var response = existing is null
            ? await client.PostAsJsonAsync("api/v2/users", payload, ct)
            : await client.PutAsJsonAsync(path, payload, ct);
        response.EnsureSuccessStatusCode();
        var actual = await ReadAsync(client, username, ct);
        if (actual is null || actual.Description != marker || actual.Status != payload.status
            || actual.HomeDirectory != payload.home_dir || actual.PublicKeys is null || !actual.PublicKeys.SequenceEqual(payload.public_keys))
            throw new InvalidOperationException("SFTPGo user read-back failed.");
    }

    private static async Task DisconnectAsync(HttpClient client, string username, CancellationToken ct)
    {
        using var response = await client.GetAsync("api/v2/connections", ct);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        foreach (var connection in document.RootElement.EnumerateArray())
        {
            if (connection.GetProperty("username").GetString() != username) continue;
            var id = connection.GetProperty("connection_id").GetString();
            if (string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException("Missing connection identity.");
            using var deletion = await client.DeleteAsync("api/v2/connections/" + Uri.EscapeDataString(id), ct);
            if (deletion.StatusCode != HttpStatusCode.NotFound) deletion.EnsureSuccessStatusCode();
        }
        using var verification = await client.GetAsync("api/v2/connections", ct);
        verification.EnsureSuccessStatusCode();
        using var observed = JsonDocument.Parse(await verification.Content.ReadAsStringAsync(ct));
        if (observed.RootElement.EnumerateArray().Any(x => x.GetProperty("username").GetString() == username))
            throw new InvalidOperationException("SFTPGo connection termination not verified.");
    }

    internal async Task DeleteAsync(Guid accountId, string username, CancellationToken ct)
    {
        if (accountId == Guid.Empty) throw new ArgumentException("Missing account identity.", nameof(accountId));
        using var client = await ConnectAsync(ct);
        var existing = await ReadAsync(client, username, ct);
        if (existing is not null && existing.Description != OwnerMarker(accountId))
            throw new InvalidOperationException("SFTPGo user ownership conflict.");
        if (existing is not null)
        {
            using var response = await client.DeleteAsync(PathFor(username), ct);
            if (response.StatusCode != HttpStatusCode.NotFound) response.EnsureSuccessStatusCode();
        }
        if (await ReadAsync(client, username, ct) is not null)
            throw new InvalidOperationException("SFTPGo user deletion not verified.");
        await DisconnectAsync(client, username, ct);
    }
}
