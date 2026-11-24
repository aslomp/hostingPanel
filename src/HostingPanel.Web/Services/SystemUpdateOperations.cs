using System.Text.Json;

namespace HostingPanel.Web.Services;

public sealed class SystemUpdateOperations(CompatibilityApiClient client)
{
    public Task<(int Status, string Body)> ListUpdatesAsync(CancellationToken ct = default) =>
        client.GetRawAsync("api/system-packages/updates", ct);

    public Task<(int Status, string Body)> HistoryAsync(CancellationToken ct = default) =>
        client.GetRawAsync("api/system-packages/history", ct);

    public static bool ValidTransaction(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public Task<(int Status, string Body)> LogAsync(string transaction, CancellationToken ct = default)
    {
        if (!ValidTransaction(transaction)) throw new ArgumentException("Invalid transaction ID.", nameof(transaction));
        return client.GetRawAsync("api/system-packages/history/" + Uri.EscapeDataString(transaction) + "/log", ct);
    }

    public static bool ValidPackages(IEnumerable<string> packages) =>
        packages.All(p => p.Length is >= 1 and <= 128 &&
            p.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '+'));

    public Task<(int Status, string Body)> CheckAsync(string[] packages, CancellationToken ct = default) =>
        SubmitAsync("api/system-packages/update-test", packages, ct);

    public Task<(int Status, string Body)> RunAsync(string[] packages, CancellationToken ct = default) =>
        SubmitAsync("api/system-packages/update-run", packages, ct);

    private Task<(int Status, string Body)> SubmitAsync(string endpoint, string[] packages, CancellationToken ct)
    {
        if (!ValidPackages(packages)) throw new ArgumentException("Invalid package name.", nameof(packages));
        return client.RequestApiAsync(HttpMethod.Post, endpoint, JsonSerializer.Serialize(new { packages }), ct);
    }
}
