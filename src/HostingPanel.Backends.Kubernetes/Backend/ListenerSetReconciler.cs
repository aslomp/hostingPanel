using System.Text.Json;
using System.Text.Json.Nodes;
using HostingPanel.Providers.CertManager;
namespace HostingPanel.Api;

// One dedicated ListenerSet, created only when at least one certificate is ready.
// Optimistic JSON Patch prevents lost updates to unrelated listeners.
internal sealed class ListenerSetReconciler(KubernetesResourceClient client, string ns)
{
    private const string Group = "gateway.networking.k8s.io";
    private const string Prefix = "hp-tls-";
    private const string IgnoreAnnotation = "cert-manager.io/ignore-tls-listeners";
    internal static string IgnoredListeners(IEnumerable<string> names) => string.Join(",", names.OrderBy(x => x, StringComparer.Ordinal));

    internal static object[] BuildUpdatePatch(JsonObject existing, JsonArray original, JsonArray next, string ignored)
    {
        var changes = new List<object>();
        if (!JsonNode.DeepEquals(original, next))
            changes.Add(new { op = "replace", path = "/spec/listeners", value = (object)next });
        var annotations = existing["metadata"]?["annotations"];
        if (annotations is null)
            changes.Add(new { op = "add", path = "/metadata/annotations", value = (object)new Dictionary<string, string> { [IgnoreAnnotation] = ignored } });
        else if (annotations[IgnoreAnnotation]?.GetValue<string>() != ignored)
            changes.Add(new { op = "add", path = "/metadata/annotations/cert-manager.io~1ignore-tls-listeners", value = (object)ignored });
        if (changes.Count > 0)
            changes.Insert(0, new { op = "test", path = "/metadata/resourceVersion", value = (object)existing["metadata"]!["resourceVersion"]!.GetValue<string>() });
        return changes.ToArray();
    }
    private static string Name(Guid id) => Prefix + id.ToString("N");
    private static bool Managed(string? name) => name is not null && name.StartsWith(Prefix, StringComparison.Ordinal) &&
        Guid.TryParseExact(name[Prefix.Length..], "N", out _);
    public async Task ReconcileAsync(string set, string gateway, string gatewayNamespace, IReadOnlyList<(Guid Id, string Host)> domains, CancellationToken ct, Guid[]? targets = null)
    {
        var desired = domains.ToDictionary(x => Name(x.Id), StringComparer.Ordinal);
        var existing = await client.GetListenerSetAsync(ns, set, ct);
        var json = existing is null ? null : JsonNode.Parse(existing)!.AsObject();
        var original = json?["spec"]?["listeners"]?.AsArray() ?? new JsonArray();
        if (json is not null &&
            (json["metadata"]?["labels"]?["hostingpanel/component"]?.GetValue<string>() != "tls-listener-set" ||
             json["spec"]?["parentRef"]?["name"]?.GetValue<string>() != gateway ||
             json["spec"]?["parentRef"]?["namespace"]?.GetValue<string>() != gatewayNamespace ||
             original.Any(listener => !Managed(listener?["name"]?.GetValue<string>()))))
            throw new InvalidOperationException("Refusing to modify a ListenerSet not exclusively owned by HostingPanel.");
        var next = new JsonArray();
        foreach (var listener in original)
            if (!Managed(listener?["name"]?.GetValue<string>())) next.Add(listener!.DeepClone());
        foreach (var pair in desired.OrderBy(x => x.Key, StringComparer.Ordinal))
            next.Add(JsonSerializer.SerializeToNode(new { name = pair.Key, hostname = pair.Value.Host, port = 443, protocol = "HTTPS",
                tls = new { mode = "Terminate", certificateRefs = new[] { new { group = "", kind = "Secret", name = CertManagerClient.Name(pair.Value.Id) + "-tls" } } },
                allowedRoutes = new { namespaces = new { from = "Same" } } }));
        var ignored = IgnoredListeners(desired.Keys);
        if (json is null && next.Count > 0)
        {
            var manifest = JsonSerializer.Serialize(new
            {
                apiVersion = Group + "/v1", kind = "ListenerSet",
                metadata = new { name = set, @namespace = ns, annotations = new Dictionary<string, string> { [IgnoreAnnotation] = ignored }, labels = new Dictionary<string, string> { ["hostingpanel/component"] = "tls-listener-set" } },
                spec = new { parentRef = new { group = Group, kind = "Gateway", name = gateway, @namespace = gatewayNamespace }, listeners = next }
            });
            await client.CreateListenerSetAsync(ns, manifest, ct);
        }
        else if (json is not null && next.Count == 0)
        {
            await client.DeleteListenerSetAsync(ns, set,
                json["metadata"]!["uid"]!.GetValue<string>(),
                json["metadata"]!["resourceVersion"]!.GetValue<string>(), ct);
        }
        else if (json is not null)
        {
            var patch = BuildUpdatePatch(json, original, next, ignored);
            if (patch.Length > 0)
                await client.PatchAsync(Group, "v1", ns, "listenersets", set, JsonSerializer.Serialize(patch), ct);
        }
        foreach (var pair in desired)
        {
            if (targets is not null && !targets.Contains(pair.Value.Id)) continue;
            var manifest = JsonSerializer.Serialize(new { apiVersion = "gateway.networking.k8s.io/v1", kind = "HTTPRoute",
                metadata = new { name = pair.Key, @namespace = ns, labels = new Dictionary<string,string> { ["hostingpanel/component"] = "tls-route" } },
                spec = new { parentRefs = new[] { new { group = "gateway.networking.k8s.io", kind = "ListenerSet", name = set, sectionName = pair.Key } },
                    hostnames = new[] { pair.Value.Host }, rules = new[] { new { backendRefs = new[] { new { name = "apache-baseline", port = 80 } } } } } });
            await client.ApplyAsync(manifest, ns, "hostingpanel-tls", ct);
        }
        if (targets is not null)
        {
            foreach (var id in targets)
            {
                var name = Name(id);
                if (desired.ContainsKey(name)) continue;
                var raw = await client.TryGetAsync(Group, "v1", ns, "httproutes", name, ct);
                if (raw is null) continue;
                using var route = JsonDocument.Parse(raw);
                var meta = route.RootElement.GetProperty("metadata");
                if (meta.TryGetProperty("labels", out var labels) &&
                    labels.TryGetProperty("hostingpanel/component", out var label) && label.GetString() == "tls-route")
                    await client.DeleteObservedAsync(Group, "v1", ns, "httproutes", route.RootElement, ct);
            }
            return;
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
