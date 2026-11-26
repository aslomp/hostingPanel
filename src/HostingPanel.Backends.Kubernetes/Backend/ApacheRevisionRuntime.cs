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
            await resources.ValidateApacheRevisionAsync(ns, pod.Name, revision, ct);
    }

    public async Task ReloadAsync(string revision, CancellationToken ct)
    {
        var pods = await GetReplicasAsync(ct);
        // Validate again against the actual replica set before sending signals.
        foreach (var pod in pods)
            await resources.ValidateApacheRevisionAsync(ns, pod.Name, revision, ct);
        foreach (var pod in pods)
            await resources.ReloadApacheAsync(ns, pod.Name, ct);
    }

    public async Task<string> GetReplicaFingerprintAsync(CancellationToken ct) =>
        JsonSerializer.Serialize(await GetReplicasAsync(ct));

    private sealed record Replica(string Name, string Uid, string ContainerId, int RestartCount);

    private async Task<Replica[]> GetReplicasAsync(CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await resources.ListPodsAsync(ns,
            "app.kubernetes.io/name=apache-baseline", ct));
        var names = new List<Replica>();
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
            if (!status.TryGetProperty("containerStatuses", out var containers))
                continue;
            foreach (var container in containers.EnumerateArray())
            {
                if (container.GetProperty("name").GetString() != "apache" ||
                    !container.TryGetProperty("containerID", out var containerId) ||
                    string.IsNullOrEmpty(containerId.GetString()) ||
                    !container.TryGetProperty("state", out var state) ||
                    !state.TryGetProperty("running", out _))
                    continue;
                names.Add(new Replica(name, metadata.GetProperty("uid").GetString()!,
                    containerId.GetString()!, container.GetProperty("restartCount").GetInt32()));
            }
        }
        if (names.Count == 0)
            throw new InvalidOperationException("No running Apache replicas available.");
        return names.OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
    }
}
