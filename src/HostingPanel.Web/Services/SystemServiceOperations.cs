using System.Text.Json;

namespace HostingPanel.Web.Services;

public sealed class SystemServiceOperations(CompatibilityApiClient client)
{
    public static bool ValidService(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
        !value.Contains("..", StringComparison.Ordinal) && value != "." &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or '@');

    public Task<(int Status, string Body)> ListAsync(CancellationToken ct = default) =>
        client.GetRawAsync("api/system-services/list", ct);

    public Task<(int Status, string Body)> DetailsAsync(string service, CancellationToken ct = default) =>
        client.GetRawAsync($"api/system-services/service/{Segment(service)}", ct);

    public Task<(int Status, string Body)> LogAsync(string service, CancellationToken ct = default) =>
        client.GetRawAsync($"api/system-services/service/{Segment(service)}/log", ct);

    public Task<(int Status, string Body)> SetWatchdogAsync(string service, bool enabled) =>
        client.RequestApiAsync(HttpMethod.Put,
            $"api/system-services-actions/service/{Segment(service)}/watchdog",
            JsonSerializer.Serialize(new { enable = enabled }));

    public Task<(int Status, string Body)> ExecuteAsync(string service, string action)
    {
        if (action is not ("restart" or "reload" or "start" or "stop"))
            throw new ArgumentException("Unsupported service action.", nameof(action));
        return client.RequestApiAsync(HttpMethod.Post,
            $"api/system-services-actions/service/{Segment(service)}/{action}");
    }

    private static string Segment(string service) => ValidService(service)
        ? Uri.EscapeDataString(service)
        : throw new ArgumentException("Invalid service name.", nameof(service));
}
