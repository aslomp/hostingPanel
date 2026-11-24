using System.Text.Json;
using System.Text.Json.Nodes;
using HostingPanel.Providers.CertManager;
namespace HostingPanel.Api;

// Existing ListenerSet only. Reserve hp-tls-<domain GUID> names for this controller.
// Optimistic JSON Patch prevents lost updates to unrelated listeners.
internal sealed class ListenerSetReconciler(KubernetesResourceClient client, string ns)
{
    private const string Group = "gateway.networking.k8s.io";
    private const string Prefix = "hp-tls-";
    private static string Name(Guid id) => Prefix + id.ToString("N");
    private static bool Managed(string? name) => name is not null && name.StartsWith(Prefix, StringComparison.Ordinal) &&
        Guid.TryParseExact(name[Prefix.Length..], "N", out _);
    public async Task ReconcileAsync(string set, IReadOnlyList<(Guid Id, string Host)> domains, CancellationToken ct)
    {
        var desired = domains.ToDictionary(x => Name(x.Id), StringComparer.Ordinal);
        var json = JsonNode.Parse(await client.GetAsync(Group, "v1", ns, "listenersets", set, ct))!.AsObject();
        var spec = json["spec"]!.AsObject();
        var original = spec["listeners"]!.AsArray();
        var next = new JsonArray();
        foreach (var listener in original)
            if (!Managed(listener?["name"]?.GetValue<string>())) next.Add(listener!.DeepClone());
        foreach (var pair in desired.OrderBy(x => x.Key, StringComparer.Ordinal))
            next.Add(JsonSerializer.SerializeToNode(new { name = pair.Key, hostname = pair.Value.Host, port = 443, protocol = "HTTPS",
                tls = new { mode = "Terminate", certificateRefs = new[] { new { group = "", kind = "Secret", name = CertManagerClient.Name(pair.Value.Id) + "-tls" } } },
                allowedRoutes = new { namespaces = new { from = "Same" } } }));
        // ListenerSet requires at least one listener; the infrastructure owner must retain a baseline listener.
        if (next.Count == 0) throw new InvalidOperationException("Existing ListenerSet must retain a non-managed baseline listener.");
        if (!JsonNode.DeepEquals(original, next))
        {
            var patch = new object[] {
                new { op = "test", path = "/metadata/resourceVersion", value = (object)json["metadata"]!["resourceVersion"]!.GetValue<string>() },
                new { op = "replace", path = "/spec/listeners", value = (object)next }
            };
            await client.PatchAsync(Group, "v1", ns, "listenersets", set, JsonSerializer.Serialize(patch), ct);
        }
        foreach (var pair in desired)
        {
            var manifest = JsonSerializer.Serialize(new { apiVersion = "gateway.networking.k8s.io/v1", kind = "HTTPRoute",
                metadata = new { name = pair.Key, @namespace = ns, labels = new Dictionary<string,string> { ["hostingpanel/component"] = "tls-route" } },
                spec = new { parentRefs = new[] { new { group = "gateway.networking.k8s.io", kind = "ListenerSet", name = set, sectionName = pair.Key } },
                    hostnames = new[] { pair.Value.Host }, rules = new[] { new { backendRefs = new[] { new { name = "apache-baseline", port = 80 } } } } } });
            await client.ApplyAsync(manifest, ns, "hostingpanel-tls", ct);
        }
        using var routes = JsonDocument.Parse(await client.ListAsync(Group, "v1", ns, "httproutes", "hostingpanel/component=tls-route", ct));
        foreach (var item in routes.RootElement.GetProperty("items").EnumerateArray())
        {
            var name = item.GetProperty("metadata").GetProperty("name").GetString()!;
            if (Managed(name) && !desired.ContainsKey(name))
                await client.DeleteAsync(Group, "v1", ns, "httproutes", name, ct);
        }
    }
}
