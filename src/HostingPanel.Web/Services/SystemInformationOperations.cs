using System.Text.Json;

namespace HostingPanel.Web.Services;

// Upstream owns the response schema; keep fixed compatibility routes out of Razor.
public sealed record SystemInformationSection(string LabelKey, int Status, string Body);

public sealed class SystemInformationOperations(CompatibilityApiClient client)
{
    private static readonly (string LabelKey, string Endpoint)[] Sources =
    [
        ("General information", "api/info"),
        ("Processor", "api/system-info/cpu"),
        ("Memory", "api/system-info/memory"),
        ("Load", "api/system-info/load"),
        ("Filesystems", "api/system-info/fs"),
        ("Uptime", "api/system-info/uptime"),
        ("Services", "api/system-info/services")
    ];

    public async Task<IReadOnlyList<SystemInformationSection>> ReadAsync(CancellationToken ct = default)
    {
        var result = new List<SystemInformationSection>(Sources.Length);
        foreach (var source in Sources)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var (status, body) = await client.GetRawAsync(source.Endpoint, ct);
                result.Add(new(source.LabelKey, status, Pretty(body)));
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException)
            {
                result.Add(new(source.LabelKey, 0, ex.Message));
            }
        }
        ct.ThrowIfCancellationRequested();
        return result;
    }

    private static string Pretty(string input)
    {
        try
        {
            using var doc = JsonDocument.Parse(input);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException) { return input; }
    }
}
