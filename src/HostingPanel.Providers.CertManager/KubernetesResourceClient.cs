using System.Net;
using System.Text.Json;
using k8s;
using k8s.Models;
using k8s.Autorest;

namespace HostingPanel.Providers.CertManager;

// Shared, lazily initialized SDK connection: disabled hosting does not require a cluster.
public sealed class KubernetesResourceClient : IDisposable
{
    private readonly Lazy<IKubernetes> client = new(() => new Kubernetes(
        KubernetesClientConfiguration.IsInCluster()
            ? KubernetesClientConfiguration.InClusterConfig()
            : KubernetesClientConfiguration.BuildConfigFromConfigFile()));
    private IKubernetes Api => client.Value;

    public async Task ApplyAsync(string manifest, string ns, string manager, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(manifest);
        var root = doc.RootElement;
        if (root.GetProperty("kind").GetString() == "List")
        {
            foreach (var item in root.GetProperty("items").EnumerateArray())
                await ApplyResourceAsync(item, ns, manager, ct);
        }
        else await ApplyResourceAsync(root, ns, manager, ct);
    }

    private async Task ApplyResourceAsync(JsonElement resource, string ns, string manager, CancellationToken ct)
    {
        var meta = resource.GetProperty("metadata");
        var name = meta.GetProperty("name").GetString()!;
        if (meta.TryGetProperty("namespace", out var actual) && actual.GetString() != ns)
            throw new InvalidOperationException("Manifest namespace does not match the configured namespace.");
        var patch = new V1Patch(resource.GetRawText(), V1Patch.PatchType.ApplyPatch);
        var kind = resource.GetProperty("kind").GetString();
        var version = resource.GetProperty("apiVersion").GetString();
        switch ((version, kind))
        {
            case ("v1", "ConfigMap"):
                await Api.CoreV1.PatchNamespacedConfigMapAsync(patch, name, ns, fieldManager: manager, cancellationToken: ct); break;
            case ("v1", "Service"):
                await Api.CoreV1.PatchNamespacedServiceAsync(patch, name, ns, fieldManager: manager, cancellationToken: ct); break;
            case ("apps/v1", "Deployment"):
                await Api.AppsV1.PatchNamespacedDeploymentAsync(patch, name, ns, fieldManager: manager, cancellationToken: ct); break;
            case ("networking.k8s.io/v1", "NetworkPolicy"):
                await Api.NetworkingV1.PatchNamespacedNetworkPolicyAsync(patch, name, ns, fieldManager: manager, cancellationToken: ct); break;
            case ("gateway.networking.k8s.io/v1", "HTTPRoute"):
                await Api.CustomObjects.PatchNamespacedCustomObjectAsync(patch, "gateway.networking.k8s.io", "v1", ns, "httproutes", name, fieldManager: manager, cancellationToken: ct); break;
            case ("cert-manager.io/v1", "Certificate"):
                await Api.CustomObjects.PatchNamespacedCustomObjectAsync(patch, "cert-manager.io", "v1", ns, "certificates", name, fieldManager: manager, cancellationToken: ct); break;
            default: throw new NotSupportedException($"Unsupported resource {version}/{kind}.");
        }
    }

    public async Task<string> GetAsync(string group, string version, string ns, string plural, string name, CancellationToken ct) =>
        KubernetesJson.Serialize(await Api.CustomObjects.GetNamespacedCustomObjectAsync(group, version, ns, plural, name, cancellationToken: ct));

    public async Task<string?> GetListenerSetAsync(string ns, string name, CancellationToken ct)
    {
        try { return await GetAsync("gateway.networking.k8s.io", "v1", ns, "listenersets", name, ct); }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound) { return null; }
    }

    public async Task CreateListenerSetAsync(string ns, string manifest, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(manifest);
        await Api.CustomObjects.CreateNamespacedCustomObjectAsync(doc.RootElement, "gateway.networking.k8s.io", "v1", ns, "listenersets", cancellationToken: ct);
    }

    public async Task DeleteListenerSetAsync(string ns, string name, string uid, string resourceVersion, CancellationToken ct)
    {
        try
        {
            await Api.CustomObjects.DeleteNamespacedCustomObjectAsync("gateway.networking.k8s.io", "v1", ns, "listenersets", name,
                body: new V1DeleteOptions { Preconditions = new V1Preconditions { Uid = uid, ResourceVersion = resourceVersion } }, cancellationToken: ct);
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound) { }
    }

    public async Task<string> ListAsync(string group, string version, string ns, string plural, string? selector, CancellationToken ct) =>
        KubernetesJson.Serialize(await Api.CustomObjects.ListNamespacedCustomObjectAsync(group, version, ns, plural, labelSelector: selector, cancellationToken: ct));

    public async Task PatchAsync(string group, string version, string ns, string plural, string name, string json, CancellationToken ct) =>
        await Api.CustomObjects.PatchNamespacedCustomObjectAsync(new V1Patch(json, V1Patch.PatchType.JsonPatch), group, version, ns, plural, name, cancellationToken: ct);

    public async Task DeleteAsync(string group, string version, string ns, string plural, string name, CancellationToken ct)
    {
        try { await Api.CustomObjects.DeleteNamespacedCustomObjectAsync(group, version, ns, plural, name, cancellationToken: ct); }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound) { }
    }

    public async Task DeleteConfigMapAsync(string ns, string name, CancellationToken ct)
    {
        try { await Api.CoreV1.DeleteNamespacedConfigMapAsync(name, ns, cancellationToken: ct); }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound) { }
    }

    // Retire only the old controller's dedicated virtualhost ConfigMaps.
    // Use UID/resourceVersion preconditions so a replaced object is never deleted.
    public async Task RetireLegacyApacheConfigMapsAsync(string ns, CancellationToken ct)
    {
        var maps = await Api.CoreV1.ListNamespacedConfigMapAsync(ns,
            labelSelector: "hostingpanel/component=vhost", cancellationToken: ct);
        foreach (var map in maps.Items)
        {
            var name = map.Metadata.Name;
            if (name is null || !name.StartsWith("site-", StringComparison.Ordinal) ||
                name.Length != 37 || !name[5..].All(char.IsAsciiHexDigitLower) ||
                map.Data is null || !map.Data.ContainsKey("vhost.conf") ||
                string.IsNullOrEmpty(map.Metadata.Uid) || string.IsNullOrEmpty(map.Metadata.ResourceVersion))
                continue;
            try
            {
                await Api.CoreV1.DeleteNamespacedConfigMapAsync(name, ns,
                    body: new V1DeleteOptions { Preconditions = new V1Preconditions
                    {
                        Uid = map.Metadata.Uid, ResourceVersion = map.Metadata.ResourceVersion
                    } }, cancellationToken: ct);
            }
            catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound) { }
        }
    }

    public async Task RetireAccountPhpResourcesAsync(string ns, IReadOnlySet<string> desired, CancellationToken ct)
    {
        const string selector = "hostingpanel/component=account-php";
        async Task RetireAsync(IEnumerable<V1ObjectMeta> objects, Func<string, V1DeleteOptions, Task> delete)
        {
            foreach (var meta in objects)
            {
                var name = meta.Name;
                if (name is null || name.Length is not (39 or 41) || !name.StartsWith("hp-fpm-", StringComparison.Ordinal) ||
                    !Guid.TryParseExact(name[7..39], "N", out _) || (name.Length == 41 && name[39..] is not ("-7" or "-8")) ||
                    desired.Contains(name) || string.IsNullOrEmpty(meta.Uid) || string.IsNullOrEmpty(meta.ResourceVersion)) continue;
                try { await delete(name, new V1DeleteOptions { Preconditions = new V1Preconditions {
                    Uid = meta.Uid, ResourceVersion = meta.ResourceVersion } }); }
                catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound) { }
            }
        }
        await RetireAsync((await Api.AppsV1.ListNamespacedDeploymentAsync(ns, labelSelector: selector, cancellationToken: ct)).Items.Select(x => x.Metadata),
            async (name, body) => { await Api.AppsV1.DeleteNamespacedDeploymentAsync(name, ns, body: body, cancellationToken: ct); });
        await RetireAsync((await Api.CoreV1.ListNamespacedServiceAsync(ns, labelSelector: selector, cancellationToken: ct)).Items.Select(x => x.Metadata),
            async (name, body) => { await Api.CoreV1.DeleteNamespacedServiceAsync(name, ns, body: body, cancellationToken: ct); });
        await RetireAsync((await Api.CoreV1.ListNamespacedConfigMapAsync(ns, labelSelector: selector, cancellationToken: ct)).Items.Select(x => x.Metadata),
            async (name, body) => { await Api.CoreV1.DeleteNamespacedConfigMapAsync(name, ns, body: body, cancellationToken: ct); });
        // Keep policies in place until the corresponding pods have terminated.
        // Policy retirement is deliberately deferred to a subsequent implementation.
    }

    public async Task<string> ListPodsAsync(string ns, string selector, CancellationToken ct) =>
        KubernetesJson.Serialize(await Api.CoreV1.ListNamespacedPodAsync(ns, labelSelector: selector, cancellationToken: ct));


    // A list/resourceVersion followed by a watch closes the startup race.
    // Reconnect is owned by the reconciler; callbacks are invalidation only.
    public async Task WatchApachePodsAsync(string ns, Action changed, CancellationToken ct)
    {
        const string selector = "app.kubernetes.io/name=apache-baseline";
        var initial = await Api.CoreV1.ListNamespacedPodAsync(ns, labelSelector: selector, cancellationToken: ct);
        var key = ns + "/apache-pods";
        ObserveList(key, KubernetesJson.Serialize(initial), _ => true, changed, ApacheFingerprint);
        var response = Api.CoreV1.ListNamespacedPodWithHttpMessagesAsync(ns,
            labelSelector: selector, resourceVersion: initial.Metadata.ResourceVersion,
            watch: true, timeoutSeconds: 300, cancellationToken: ct);
        await foreach (var (kind, item) in response.WatchAsync<V1Pod, V1PodList>(
            onError: error => throw new InvalidOperationException("Apache pod watch failed.", error),
            cancellationToken: ct))
        {
            ObserveEvent(key, kind, item, _ => true, changed, ApacheFingerprint);
        }
    }

    // Custom resources use the same list/watch recovery contract as pods.
    public async Task WatchCustomResourcesAsync(string group, string version, string ns,
        string plural, string selector, Action reset, Action<string> changed, CancellationToken ct)
    {
        using var initial = JsonDocument.Parse(await ListAsync(group, version, ns, plural, selector, ct));
        var resourceVersion = initial.RootElement.GetProperty("metadata").GetProperty("resourceVersion").GetString();
        reset();
        var response = Api.CustomObjects.ListNamespacedCustomObjectWithHttpMessagesAsync(
            group, version, ns, plural, labelSelector: selector, resourceVersion: resourceVersion,
            watch: true, timeoutSeconds: 300, cancellationToken: ct);
        await foreach (var (kind, item) in response.WatchAsync<object, object>(
            onError: error => throw new InvalidOperationException("Custom resource watch failed.", error),
            cancellationToken: ct))
        {
            if (kind == WatchEventType.Error)
                throw new InvalidOperationException("Custom resource watch returned an error.");
            if (kind is WatchEventType.Added or WatchEventType.Modified or WatchEventType.Deleted)
            {
                using var doc = JsonDocument.Parse(KubernetesJson.Serialize(item));
                changed(doc.RootElement.GetProperty("metadata").GetProperty("name").GetString()!);
            }
        }
    }

    // Certificate status queries remain independent. Only spec/identity/readiness
    // transitions enqueue controller work; other certificate events invalidate UI reads.
    public Task WatchCertificateStateAsync(string ns, Action reset, Action<string> changed,
        Action statusChanged, CancellationToken ct) =>
        WatchRelevantCustomAsync("cert-manager.io", ns, "certificates", "hostingpanel/component=certificate",
            null, CertificateFingerprint, reset, changed, statusChanged, ct);

    public Task WatchTlsRoutingAsync(string ns, string plural, string? name, Action reset,
        Action<string> changed, CancellationToken ct)
    {
        if (plural == "listenersets" && !string.IsNullOrEmpty(name))
            return WatchRelevantCustomAsync("gateway.networking.k8s.io", ns, plural, null,
                "metadata.name=" + name, RoutingFingerprint, reset, changed, null, ct);
        if (plural == "httproutes" && name is null)
            return WatchRelevantCustomAsync("gateway.networking.k8s.io", ns, plural, "hostingpanel/component=tls-route",
                null, RoutingFingerprint, reset, changed, null, ct);
        throw new ArgumentException("Unsupported TLS routing watch.");
    }

    public static string CertificateFingerprint(JsonElement item)
    {
        var meta = item.GetProperty("metadata");
        var ready = item.TryGetProperty("status", out var status) && status.TryGetProperty("conditions", out var conditions) &&
            conditions.EnumerateArray().Any(condition =>
                condition.GetProperty("type").GetString() == "Ready" && condition.GetProperty("status").GetString() == "True" &&
                (!condition.TryGetProperty("observedGeneration", out var observed) ||
                 !meta.TryGetProperty("generation", out var generation) || JsonElement.DeepEquals(observed, generation)));
        return Fingerprint(item) + "|" + ready;
    }

    public static string RoutingFingerprint(JsonElement item)
    {
        var meta = item.GetProperty("metadata");
        var ignored = meta.TryGetProperty("annotations", out var annotations) &&
            annotations.TryGetProperty("cert-manager.io/ignore-tls-listeners", out var value) ? value.GetString() : null;
        return Fingerprint(item) + "|" + ignored;
    }

    private async Task WatchRelevantCustomAsync(string group, string ns, string plural,
        string? selector, string? fieldSelector, Func<JsonElement, string> fingerprint,
        Action reset, Action<string> changed, Action? statusChanged, CancellationToken ct)
    {
        // The snapshot survives normal reconnects. A relist only schedules work
        // when relevant state changed, including deletions while disconnected.
        var key = ns + "/tls/" + plural + "/" + fieldSelector;
        var list = await Api.CustomObjects.ListNamespacedCustomObjectAsync(group, "v1", ns, plural,
            labelSelector: selector, fieldSelector: fieldSelector, cancellationToken: ct);
        var json = KubernetesJson.Serialize(list);
        using var initial = JsonDocument.Parse(json);
        ObserveList(key, json, _ => true, reset, fingerprint);
        if (statusChanged is not null)
            ObserveList(key + "/status", json, _ => true, statusChanged, StatusFingerprint);
        var response = Api.CustomObjects.ListNamespacedCustomObjectWithHttpMessagesAsync(group, "v1", ns, plural,
            labelSelector: selector, fieldSelector: fieldSelector,
            resourceVersion: initial.RootElement.GetProperty("metadata").GetProperty("resourceVersion").GetString(),
            watch: true, timeoutSeconds: 300, cancellationToken: ct);
        await foreach (var (kind, item) in response.WatchAsync<object, object>(
            onError: error => throw new InvalidOperationException("TLS resource watch failed.", error), cancellationToken: ct))
        {
            if (kind == WatchEventType.Error) throw new InvalidOperationException("TLS resource watch returned an error.");
            if (kind is not (WatchEventType.Added or WatchEventType.Modified or WatchEventType.Deleted)) continue;
            using var doc = JsonDocument.Parse(KubernetesJson.Serialize(item));
            var resource = doc.RootElement;
            var name = resource.GetProperty("metadata").GetProperty("name").GetString()!;
            var snapshot = watchSnapshots[key];
            if (kind == WatchEventType.Deleted)
            {
                if (snapshot.Remove(name)) changed(name);
            }
            else
            {
                var next = fingerprint(resource);
                if (!snapshot.TryGetValue(name, out var previous) || next != previous)
                {
                    snapshot[name] = next;
                    changed(name);
                }
            }
            if (statusChanged is not null)
            {
                var statuses = watchSnapshots[key + "/status"];
                if (kind == WatchEventType.Deleted)
                {
                    if (statuses.Remove(name)) statusChanged();
                }
                else
                {
                    var next = StatusFingerprint(resource);
                    if (!statuses.TryGetValue(name, out var previous) || next != previous)
                    {
                        statuses[name] = next;
                        statusChanged();
                    }
                }
            }
        }
    }

    private static string StatusFingerprint(JsonElement item) =>
        Fingerprint(item) + "|" + (item.TryGetProperty("status", out var status) ? status.GetRawText() : "");

    // Compare desired-state fingerprints, not status or resourceVersion. A normal
    // relist or a controller's status update must not invalidate workloads.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Dictionary<string, string>> watchSnapshots = new();

    private static Dictionary<string, string> Snapshot(string json, Func<JsonElement, bool> include, Func<JsonElement, string>? fingerprint = null)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("items").EnumerateArray().Where(include)
            .ToDictionary(x => x.GetProperty("metadata").GetProperty("name").GetString()!, fingerprint ?? Fingerprint);
    }

    private static string ApacheFingerprint(JsonElement item)
    {
        var meta = item.GetProperty("metadata");
        var status = item.TryGetProperty("status", out var value) ? value : default;
        var containers = status.ValueKind == JsonValueKind.Object && status.TryGetProperty("containerStatuses", out var array)
            ? array.EnumerateArray().Where(x => x.GetProperty("name").GetString() == "apache").Select(x => new {
                Id = x.TryGetProperty("containerID", out var id) ? id.GetString() : null,
                Restarts = x.TryGetProperty("restartCount", out var count) ? count.GetInt32() : 0,
                Running = x.TryGetProperty("state", out var state) && state.TryGetProperty("running", out _)
            }).ToArray() : null;
        return JsonSerializer.Serialize(new {
            Uid = meta.GetProperty("uid").GetString(),
            Deleting = meta.TryGetProperty("deletionTimestamp", out var deletion) && deletion.ValueKind != JsonValueKind.Null,
            Phase = status.ValueKind == JsonValueKind.Object && status.TryGetProperty("phase", out var phase) ? phase.GetString() : null,
            Containers = containers
        });
    }

    private static string Fingerprint(JsonElement item)
    {
        var meta = item.GetProperty("metadata");
        return string.Join("|", new[] {
            meta.GetProperty("uid").GetString(),
            item.TryGetProperty("spec", out var spec) ? spec.GetRawText() : "",
            item.TryGetProperty("data", out var data) ? data.GetRawText() : "",
            meta.TryGetProperty("labels", out var labels) ? labels.GetRawText() : ""
        });
    }

    private void ObserveList(string key, string json, Func<JsonElement, bool> include, Action changed, Func<JsonElement, string>? fingerprint = null)
    {
        var next = Snapshot(json, include, fingerprint);
        if (!watchSnapshots.TryGetValue(key, out var previous) || next.Count != previous.Count ||
            next.Any(x => !previous.TryGetValue(x.Key, out var value) || value != x.Value))
            changed();
        watchSnapshots[key] = next;
    }

    private void ObserveEvent(string key, WatchEventType kind, object item, Func<JsonElement, bool> include, Action changed, Func<JsonElement, string>? project = null)
    {
        if (kind == WatchEventType.Error) throw new InvalidOperationException("Website resource watch failed.");
        if (kind is not (WatchEventType.Added or WatchEventType.Modified or WatchEventType.Deleted)) return;
        using var doc = JsonDocument.Parse(KubernetesJson.Serialize(item));
        var resource = doc.RootElement;
        var name = resource.GetProperty("metadata").GetProperty("name").GetString()!;
        var snapshot = watchSnapshots[key];
        if (kind == WatchEventType.Deleted || !include(resource))
        {
            if (snapshot.Remove(name)) changed();
            return;
        }
        var fingerprint = (project ?? Fingerprint)(resource);
        if (!snapshot.TryGetValue(name, out var previous) || previous != fingerprint)
        {
            snapshot[name] = fingerprint;
            changed();
        }
    }

    public async Task WatchWebsiteRoutesAsync(string ns, Action changed, CancellationToken ct)
    {
        const string group = "gateway.networking.k8s.io", version = "v1", plural = "httproutes";
        var key = ns + "/website-routes";
        bool Include(JsonElement x) {
            var name = x.GetProperty("metadata").GetProperty("name").GetString()!;
            return name.StartsWith("site-", StringComparison.Ordinal) && Guid.TryParseExact(name[5..], "N", out _);
        }
        var json = await ListAsync(group, version, ns, plural, null, ct);
        using var doc = JsonDocument.Parse(json);
        ObserveList(key, json, Include, changed);
        var response = Api.CustomObjects.ListNamespacedCustomObjectWithHttpMessagesAsync(group, version, ns, plural,
            resourceVersion: doc.RootElement.GetProperty("metadata").GetProperty("resourceVersion").GetString(),
            watch: true, timeoutSeconds: 300, cancellationToken: ct);
        await foreach (var (kind, item) in response.WatchAsync<object, object>(onError: e => throw new InvalidOperationException("Route watch failed.", e), cancellationToken: ct))
            ObserveEvent(key, kind, item, Include, changed);
    }

    public async Task WatchAccountPhpAsync(string ns, Action changed, CancellationToken ct)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tasks = new[] { WatchPhpDeploymentsAsync(ns, changed, lifetime.Token), WatchPhpServicesAsync(ns, changed, lifetime.Token),
            WatchPhpConfigMapsAsync(ns, changed, lifetime.Token), WatchPhpPoliciesAsync(ns, changed, lifetime.Token) };
        try { await await Task.WhenAny(tasks); }
        finally
        {
            await lifetime.CancelAsync();
            try { await Task.WhenAll(tasks); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }
    }

    private async Task WatchPhpDeploymentsAsync(string ns, Action changed, CancellationToken ct)
    {
        const string selector = "hostingpanel/component=account-php";
        var key = ns + "/php-deployments";
        var initial = await Api.AppsV1.ListNamespacedDeploymentAsync(ns, labelSelector: selector, cancellationToken: ct);
        ObserveList(key, KubernetesJson.Serialize(initial), _ => true, changed);
        var response = Api.AppsV1.ListNamespacedDeploymentWithHttpMessagesAsync(ns, labelSelector: selector,
            resourceVersion: initial.Metadata.ResourceVersion, watch: true, timeoutSeconds: 300, cancellationToken: ct);
        await foreach (var (kind, item) in response.WatchAsync<V1Deployment, V1DeploymentList>(onError: e => throw new InvalidOperationException("PHP watch failed.", e), cancellationToken: ct))
            ObserveEvent(key, kind, item, _ => true, changed);
    }

    private async Task WatchPhpServicesAsync(string ns, Action changed, CancellationToken ct)
    {
        const string selector = "hostingpanel/component=account-php";
        var key = ns + "/php-services";
        var initial = await Api.CoreV1.ListNamespacedServiceAsync(ns, labelSelector: selector, cancellationToken: ct);
        ObserveList(key, KubernetesJson.Serialize(initial), _ => true, changed);
        var response = Api.CoreV1.ListNamespacedServiceWithHttpMessagesAsync(ns, labelSelector: selector,
            resourceVersion: initial.Metadata.ResourceVersion, watch: true, timeoutSeconds: 300, cancellationToken: ct);
        await foreach (var (kind, item) in response.WatchAsync<V1Service, V1ServiceList>(onError: e => throw new InvalidOperationException("PHP watch failed.", e), cancellationToken: ct))
            ObserveEvent(key, kind, item, _ => true, changed);
    }

    private async Task WatchPhpConfigMapsAsync(string ns, Action changed, CancellationToken ct)
    {
        const string selector = "hostingpanel/component=account-php";
        var key = ns + "/php-configmaps";
        var initial = await Api.CoreV1.ListNamespacedConfigMapAsync(ns, labelSelector: selector, cancellationToken: ct);
        ObserveList(key, KubernetesJson.Serialize(initial), _ => true, changed);
        var response = Api.CoreV1.ListNamespacedConfigMapWithHttpMessagesAsync(ns, labelSelector: selector,
            resourceVersion: initial.Metadata.ResourceVersion, watch: true, timeoutSeconds: 300, cancellationToken: ct);
        await foreach (var (kind, item) in response.WatchAsync<V1ConfigMap, V1ConfigMapList>(onError: e => throw new InvalidOperationException("PHP watch failed.", e), cancellationToken: ct))
            ObserveEvent(key, kind, item, _ => true, changed);
    }

    private async Task WatchPhpPoliciesAsync(string ns, Action changed, CancellationToken ct)
    {
        const string selector = "hostingpanel/component=account-php";
        var key = ns + "/php-policies";
        var initial = await Api.NetworkingV1.ListNamespacedNetworkPolicyAsync(ns, labelSelector: selector, cancellationToken: ct);
        ObserveList(key, KubernetesJson.Serialize(initial), _ => true, changed);
        var response = Api.NetworkingV1.ListNamespacedNetworkPolicyWithHttpMessagesAsync(ns, labelSelector: selector,
            resourceVersion: initial.Metadata.ResourceVersion, watch: true, timeoutSeconds: 300, cancellationToken: ct);
        await foreach (var (kind, item) in response.WatchAsync<V1NetworkPolicy, V1NetworkPolicyList>(onError: e => throw new InvalidOperationException("PHP watch failed.", e), cancellationToken: ct))
            ObserveEvent(key, kind, item, _ => true, changed);
    }

    public async Task<string?> TryGetAsync(string group, string version, string ns, string plural, string name, CancellationToken ct)
    {
        try { return await GetAsync(group, version, ns, plural, name, ct); }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound) { return null; }
    }

    public async Task DeleteObservedAsync(string group, string version, string ns, string plural, JsonElement item, CancellationToken ct)
    {
        var meta = item.GetProperty("metadata");
        try
        {
            await Api.CustomObjects.DeleteNamespacedCustomObjectAsync(group, version, ns, plural,
                meta.GetProperty("name").GetString()!, body: new V1DeleteOptions { Preconditions = new V1Preconditions
                { Uid = meta.GetProperty("uid").GetString(), ResourceVersion = meta.GetProperty("resourceVersion").GetString() } }, cancellationToken: ct);
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound) { }
    }

    // Deliberately expose only the two platform Apache operations, not a
    // general-purpose command execution API.
    public Task ValidateApacheRevisionAsync(string ns, string pod, string revision, CancellationToken ct)
    {
        if (revision.Length != 64 || !revision.All(char.IsAsciiHexDigitLower))
            throw new ArgumentException("Invalid Apache revision.", nameof(revision));
        return ApacheExecAsync(ns, pod, new[] {
            "httpd", "-t", "-f", "/etc/hostingpanel/httpd.conf",
            "-D", "HOSTINGPANEL_CANDIDATE",
            "-c", "IncludeOptional /storage/apache-config/" + revision + "/*.conf"
        }, ct);
    }

    public async Task ReloadApacheAsync(string ns, string pod, CancellationToken ct)
    {
        await ApacheExecAsync(ns, pod, new[] { "httpd", "-t", "-f", "/etc/hostingpanel/httpd.conf" }, ct);
        await ApacheExecAsync(ns, pod, new[] { "httpd", "-k", "graceful", "-f", "/etc/hostingpanel/httpd.conf" }, ct);
    }

    private async Task ApacheExecAsync(string ns, string pod, string[] command, CancellationToken ct)
    {
        var target = await Api.CoreV1.ReadNamespacedPodAsync(pod, ns, cancellationToken: ct);
        if (target.Metadata.DeletionTimestamp is not null ||
            (target.Metadata.Labels is null || !target.Metadata.Labels.TryGetValue("app.kubernetes.io/name", out var label) || label != "apache-baseline") ||
            target.Spec.Containers.Count != 1 || target.Spec.Containers[0].Name != "apache" ||
            target.Status.Phase != "Running")
            throw new InvalidOperationException("Apache replica is not available for configuration activation.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var exit = await Api.NamespacedPodExecAsync(pod, ns, "apache", command, false,
            async (_, stdout, stderr) =>
            {
                await Task.WhenAll(stdout.CopyToAsync(Stream.Null, timeout.Token),
                    stderr.CopyToAsync(Stream.Null, timeout.Token));
            }, timeout.Token);
        if (exit != 0)
            throw new InvalidOperationException("Apache configuration operation failed with exit code " + exit);
    }

    public void Dispose() { if (client.IsValueCreated) client.Value.Dispose(); }
}
