using System.Text.Json;
using HostingPanel.Providers.CertManager;

namespace HostingPanel.Api;

/// <summary>Validates and signals every running shared Apache replica.
/// Deployment authorization must be restricted independently of pod labels.</summary>
public sealed class ApacheRevisionRuntime(KubernetesResourceClient resources, string ns)
{
    public async Task ValidateAsync(string revision, CancellationToken ct)
    {
        foreach (var pod in await GetReplicasAsync(ct))
            await resources.ValidateApacheRevisionAsync(ns, pod, revision, ct);
    }

    public async Task ReloadAsync(string revision, CancellationToken ct)
    {
        var pods = await GetReplicasAsync(ct);
        // Validate again against the actual replica set before sending signals.
        foreach (var pod in pods)
            await resources.ValidateApacheRevisionAsync(ns, pod, revision, ct);
        foreach (var pod in pods)
            await resources.ReloadApacheAsync(ns, pod, ct);
    }

    private async Task<string[]> GetReplicasAsync(CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await resources.ListPodsAsync(ns,
            "app.kubernetes.io/name=apache-baseline", ct));
        var names = new List<string>();
        foreach (var pod in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            var metadata = pod.GetProperty("metadata");
            if (metadata.TryGetProperty("deletionTimestamp", out _)) continue;
            var name = metadata.GetProperty("name").GetString()!;
            if (!name.StartsWith("apache-baseline-", StringComparison.Ordinal) ||
                !int.TryParse(name["apache-baseline-".Length..], out var ordinal) || ordinal < 0 ||
                name != $"apache-baseline-{ordinal}")
                throw new InvalidOperationException("Legacy Apache workload still present; wait for Helm migration.");
            if (!pod.TryGetProperty("status", out var status) ||
                !status.TryGetProperty("phase", out var phase) || phase.GetString() != "Running")
                continue;
            names.Add(name);
        }
        if (names.Count == 0)
            throw new InvalidOperationException("No running Apache replicas available.");
        return names.Order(StringComparer.Ordinal).ToArray();
    }
}
