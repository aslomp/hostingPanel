using System.Text.Json;

namespace HostingPanel.Web.Services;

public enum ServerSettingsSection { Active, Defaults, Local, Database, Email, OutboundFilter, Timezone }

public sealed class ServerSettingsOperations(CompatibilityApiClient client)
{
    public Task<(int Status, string Body)> ReadAsync(ServerSettingsSection section, CancellationToken ct = default) =>
        client.GetRawAsync("api/server-settings/" + (section switch
        {
            ServerSettingsSection.Active => "directadmin-conf/active",
            ServerSettingsSection.Defaults => "directadmin-conf/default",
            ServerSettingsSection.Local => "directadmin-conf/local",
            ServerSettingsSection.Database => "db-config",
            ServerSettingsSection.Email => "email/config",
            ServerSettingsSection.OutboundFilter => "email/outbound-filter",
            ServerSettingsSection.Timezone => "timezone/current",
            _ => throw new ArgumentOutOfRangeException(nameof(section))
        }), ct);

    public Task<(int Status, string Body)> ListTimezonesAsync(CancellationToken ct = default) =>
        client.GetRawAsync("api/server-settings/timezone/list", ct);

    public static bool ValidDatabaseConfig(string input)
    {
        using var json = JsonDocument.Parse(input);
        return json.RootElement.ValueKind == JsonValueKind.Object &&
            new[] { "address", "password", "tls", "tlsVerify", "unixSocket", "user" }
                .All(key => json.RootElement.TryGetProperty(key, out _));
    }

    public static bool ValidPatch(string input)
    {
        using var json = JsonDocument.Parse(input);
        return json.RootElement.ValueKind == JsonValueKind.Object;
    }

    public Task<(int Status, string Body)> TestDatabaseAsync(string json) =>
        client.RequestApiAsync(HttpMethod.Post, "api/server-settings/db-config-test", json);

    public Task<(int Status, string Body)> SaveDatabaseAsync(string json) =>
        client.RequestApiAsync(HttpMethod.Put, "api/server-settings/db-config", json);

    public Task<(int Status, string Body)> ChangeHostnameAsync(string hostname) =>
        client.RequestApiAsync(HttpMethod.Post, "api/server-settings/change-hostname", JsonSerializer.Serialize(new { hostname }));

    public Task<(int Status, string Body)> ChangeTimezoneAsync(string timezone) =>
        client.RequestApiAsync(HttpMethod.Post, "api/server-settings/timezone/set", JsonSerializer.Serialize(new { tz = timezone }));

    public Task<(int Status, string Body)> PatchAsync(string json) =>
        client.RequestApiAsync(HttpMethod.Patch, "api/server-settings/directadmin-conf/local", json);
}
