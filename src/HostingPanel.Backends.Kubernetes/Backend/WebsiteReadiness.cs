using System.Text.Json;

namespace HostingPanel.Api;

public sealed record WebsiteObservation(string Apache, string Php, string Route)
{
    public bool Ready => Apache == "Ready" && (Php is "Ready" or "Not required") && Route == "Ready";
}

public sealed class WebsiteReadiness
{
    private readonly object gate = new();
    private Dictionary<Guid, WebsiteObservation> observations = new();
    private DateTimeOffset observedAt;
    public WebsiteObservation? Get(Guid id)
    {
        lock (gate)
            return DateTimeOffset.UtcNow - observedAt < TimeSpan.FromSeconds(90)
                ? observations.GetValueOrDefault(id) : null;
    }

    public bool Replace(Dictionary<Guid, WebsiteObservation> next)
    {
        lock (gate)
        {
            var changed = observations.Count != next.Count ||
                next.Any(x => !observations.TryGetValue(x.Key, out var previous) || previous != x.Value);
            observations = next;
            observedAt = DateTimeOffset.UtcNow;
            return changed;
        }
    }

    private static long Number(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) && value.TryGetInt64(out var number) ? number : 0;

    public static string Deployment(JsonElement resource)
    {
        if (!resource.TryGetProperty("status", out var status)) return "Pending";
        var generation = Number(resource.GetProperty("metadata"), "generation");
        if (generation == 0 || Number(status, "observedGeneration") < generation) return "Pending";
        if (status.TryGetProperty("conditions", out var conditions) &&
            conditions.EnumerateArray().Any(c =>
                (c.GetProperty("type").GetString() == "Progressing" && c.GetProperty("status").GetString() == "False") ||
                (c.GetProperty("type").GetString() == "ReplicaFailure" && c.GetProperty("status").GetString() == "True")))
            return "Failed";
        var replicas = Number(resource.GetProperty("spec"), "replicas");
        return replicas > 0 && Number(status, "updatedReplicas") == replicas &&
            Number(status, "readyReplicas") == replicas && Number(status, "availableReplicas") == replicas &&
            Number(status, "replicas") == replicas ? "Ready" : "Pending";
    }

    public static string Route(JsonElement resource, string gateway, string gatewayNamespace, string listener)
    {
        var generation = Number(resource.GetProperty("metadata"), "generation");
        if (generation == 0 || !resource.TryGetProperty("status", out var status) ||
            !status.TryGetProperty("parents", out var parents)) return "Pending";
        foreach (var parent in parents.EnumerateArray())
        {
            var reference = parent.GetProperty("parentRef");
            if (reference.GetProperty("name").GetString() != gateway ||
                !reference.TryGetProperty("namespace", out var ns) || ns.GetString() != gatewayNamespace ||
                !reference.TryGetProperty("sectionName", out var section) || section.GetString() != listener) continue;
            var conditions = parent.GetProperty("conditions").EnumerateArray()
                .Where(x => Number(x, "observedGeneration") == generation).ToArray();
            if (conditions.Any(x => (x.GetProperty("type").GetString() is "Accepted" or "ResolvedRefs") &&
                x.GetProperty("status").GetString() == "False")) return "Failed";
            return new[] { "Accepted", "ResolvedRefs" }.All(type => conditions.Any(x =>
                x.GetProperty("type").GetString() == type && x.GetProperty("status").GetString() == "True"))
                ? "Ready" : "Pending";
        }
        return "Pending";
    }
}
