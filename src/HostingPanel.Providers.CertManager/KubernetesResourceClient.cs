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

    // Only the fixed account ConfigMap changes. Never patch the pod template for account operations.
    public async Task<bool> ActivateOpenSshAsync(string ns, string name, string revision,
        IReadOnlyDictionary<string,string> data, CancellationToken ct)
    {
        if (revision.Length != 64 || !revision.All(char.IsAsciiHexDigitLower))
            throw new ArgumentException("Invalid SFTP revision.", nameof(revision));
        var mapName = name + "-accounts";
        var manifest = JsonSerializer.Serialize(new { apiVersion = "v1", kind = "ConfigMap",
            metadata = new { name = mapName, labels = new Dictionary<string,string> {
                ["hostingpanel/component"] = "openssh-accounts", ["hostingpanel/workload"] = name } },
            data });
        await ApplyAsync(manifest, ns, "hostingpanel-sftp", ct);
        var pods = await Api.CoreV1.ListNamespacedPodAsync(ns, labelSelector: "hostingpanel/sftp-workload=" + name, cancellationToken: ct);
        var pod = pods.Items.SingleOrDefault(x => x.Metadata.Name == name + "-0");
        if (pods.Items.Count != 1 || pod is null || pod.Metadata.DeletionTimestamp is not null ||
            pod.Spec.Containers.Count != 1 || pod.Spec.Containers[0].Name != "openssh" ||
            pod.Status?.Conditions?.Any(x => x.Type == "Ready" && x.Status == "True") != true)
            return false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var output = new MemoryStream();
        var exit = await Api.NamespacedPodExecAsync(pod.Metadata.Name, ns, "openssh",
            new[] { "/bin/cat", "/etc/hostingpanel/revision" }, false,
            async (_, stdout, stderr) => await Task.WhenAll(stdout.CopyToAsync(output, timeout.Token),
                stderr.CopyToAsync(Stream.Null, timeout.Token)), timeout.Token);
        return exit == 0 && System.Text.Encoding.UTF8.GetString(output.ToArray()).Trim() == revision;
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


    // Each stream owns a cursor and an independent reconnect lifecycle.
    // The cursor advances only after the event callback succeeds.
    private async Task RunWatchAsync<T>(Func<CancellationToken, Task<string>> list,
        Func<string, CancellationToken, IAsyncEnumerable<(WatchEventType, T)>> watch,
        Action<string> reset, Action<WatchEventType, T> observe, CancellationToken ct)
    {
        string? cursor = null;
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (cursor is null)
                {
                    var json = await list(ct);
                    using var doc = JsonDocument.Parse(json);
                    reset(json);
                    cursor = doc.RootElement.GetProperty("metadata").GetProperty("resourceVersion").GetString()
                        ?? throw new InvalidOperationException("Missing list resourceVersion.");
                }
                var started = System.Diagnostics.Stopwatch.StartNew();
                await foreach (var (kind, item) in watch(cursor, ct).WithCancellation(ct))
                {
                    using var doc = JsonDocument.Parse(KubernetesJson.Serialize(item));
                    var resource = doc.RootElement;
                    if (kind == WatchEventType.Error)
                    {
                        if (resource.TryGetProperty("code", out var code) && code.TryGetInt32(out var status) && status == 410)
                        {
                            cursor = null;
                            break;
                        }
                        throw new InvalidOperationException("Kubernetes watch returned an error.");
                    }
                    if (kind is WatchEventType.Added or WatchEventType.Modified or WatchEventType.Deleted)
                    {
                        try { observe(kind, item); }
                        catch { cursor = null; throw; }
                    }
                    if (resource.TryGetProperty("metadata", out var metadata) &&
                        metadata.TryGetProperty("resourceVersion", out var rv) && rv.GetString() is { Length: > 0 } next)
                        cursor = next;
                    failures = 0;
                }
                // A normal server timeout is not a failure. Avoid a tight loop
                // if a proxy closes the stream immediately.
                if (started.Elapsed >= TimeSpan.FromSeconds(10)) failures = 0;
                else failures = Math.Min(failures + 1, 7);
                await Task.Delay(TimeSpan.FromSeconds(failures == 0 ? 0.25 : Math.Min(60, Math.Pow(2, failures - 1))), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                if (WatchCursorExpired(ex)) cursor = null;
                failures = Math.Min(failures + 1, 7);
                System.Diagnostics.Trace.TraceWarning("Kubernetes watch disconnected: {0}", ex.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, failures - 1))), ct);
            }
        }
    }

    private static bool WatchCursorExpired(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
            if (current is HttpOperationException http && http.Response?.StatusCode == HttpStatusCode.Gone)
                return true;
        return false;
    }

    public Task<Stream> FollowApacheLogAsync(string ns, string name, CancellationToken ct) =>
        Api.CoreV1.ReadNamespacedPodLogAsync(name, ns, container: "apache", follow: true, timestamps: true, cancellationToken: ct);

    // Independent observer: do not share the website reconciler's snapshot cache.
    public Task WatchApacheLogPodsAsync(string ns, Action<string> snapshot, Action<WatchEventType, string> observe, CancellationToken ct)
    {
        const string selector = "app.kubernetes.io/name=apache-baseline";
        return RunWatchAsync<V1Pod>(
            token => ListPodsAsync(ns, selector, token),
            (rv, token) => Api.CoreV1.ListNamespacedPodWithHttpMessagesAsync(ns, labelSelector: selector,
                resourceVersion: rv, allowWatchBookmarks: true, watch: true, timeoutSeconds: 300, cancellationToken: token)
                .WatchAsync<V1Pod, V1PodList>(onError: e => throw new InvalidOperationException("Apache log pod watch failed.", e), cancellationToken: token),
            snapshot, (kind, item) => observe(kind, KubernetesJson.Serialize(item)), ct);
    }

    public Task WatchApachePodsAsync(string ns, Action changed, CancellationToken ct)
    {
        const string selector = "app.kubernetes.io/name=apache-baseline";
        var key = ns + "/apache-pods";
        return RunWatchAsync<V1Pod>(
            async token => KubernetesJson.Serialize(await Api.CoreV1.ListNamespacedPodAsync(ns, labelSelector: selector, cancellationToken: token)),
            (rv, token) => Api.CoreV1.ListNamespacedPodWithHttpMessagesAsync(ns, labelSelector: selector,
                resourceVersion: rv, allowWatchBookmarks: true, watch: true, timeoutSeconds: 300, cancellationToken: token)
                .WatchAsync<V1Pod, V1PodList>(onError: e => throw new InvalidOperationException("Apache watch failed.", e), cancellationToken: token),
            json => ObserveList(key, json, _ => true, changed, ApacheFingerprint),
            (kind, item) => ObserveEvent(key, kind, item, _ => true, changed, ApacheFingerprint), ct);
    }

    public Task WatchCustomResourcesAsync(string group, string version, string ns,
        string plural, string selector, Action reset, Action<string> changed, CancellationToken ct) =>
        RunWatchAsync<object>(
            token => ListAsync(group, version, ns, plural, selector, token),
            (rv, token) => Api.CustomObjects.ListNamespacedCustomObjectWithHttpMessagesAsync(group, version, ns, plural,
                labelSelector: selector, resourceVersion: rv, allowWatchBookmarks: true, watch: true, timeoutSeconds: 300, cancellationToken: token)
                .WatchAsync<object, object>(onError: e => throw new InvalidOperationException("Custom watch failed.", e), cancellationToken: token),
            _ => reset(), (kind, item) => {
                using var doc = JsonDocument.Parse(KubernetesJson.Serialize(item));
                changed(doc.RootElement.GetProperty("metadata").GetProperty("name").GetString()!);
            }, ct);

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

    private Task WatchRelevantCustomAsync(string group, string ns, string plural,
        string? selector, string? fieldSelector, Func<JsonElement, string> fingerprint,
        Action reset, Action<string> changed, Action? statusChanged, CancellationToken ct)
    {
        var key = ns + "/tls/" + plural + "/" + fieldSelector;
        return RunWatchAsync<object>(
            async token => KubernetesJson.Serialize(await Api.CustomObjects.ListNamespacedCustomObjectAsync(group, "v1", ns, plural,
                labelSelector: selector, fieldSelector: fieldSelector, cancellationToken: token)),
            (rv, token) => Api.CustomObjects.ListNamespacedCustomObjectWithHttpMessagesAsync(group, "v1", ns, plural,
                labelSelector: selector, fieldSelector: fieldSelector, resourceVersion: rv,
                allowWatchBookmarks: true, watch: true, timeoutSeconds: 300, cancellationToken: token)
                .WatchAsync<object, object>(onError: e => throw new InvalidOperationException("TLS watch failed.", e), cancellationToken: token),
            json => {
                ObserveList(key, json, _ => true, reset, fingerprint);
                if (statusChanged is not null) ObserveList(key + "/status", json, _ => true, statusChanged, StatusFingerprint);
            },
            (kind, item) =>
            {
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
            }, ct);
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

    public Task WatchWebsiteRoutesAsync(string ns, Action changed, CancellationToken ct)
    {
        const string group = "gateway.networking.k8s.io", version = "v1", plural = "httproutes";
        var key = ns + "/website-routes";
        bool Include(JsonElement x) {
            var name = x.GetProperty("metadata").GetProperty("name").GetString()!;
            return name.StartsWith("site-", StringComparison.Ordinal) && Guid.TryParseExact(name[5..], "N", out _);
        }
        return RunWatchAsync<object>(
            token => ListAsync(group, version, ns, plural, null, token),
            (rv, token) => Api.CustomObjects.ListNamespacedCustomObjectWithHttpMessagesAsync(group, version, ns, plural,
                resourceVersion: rv, allowWatchBookmarks: true, watch: true, timeoutSeconds: 300, cancellationToken: token)
                .WatchAsync<object, object>(onError: e => throw new InvalidOperationException("Route watch failed.", e), cancellationToken: token),
            json => ObserveList(key, json, Include, changed),
            (kind, item) => ObserveEvent(key, kind, item, Include, changed), ct);
    }

    public Task WatchAccountPhpAsync(string ns, Action changed, CancellationToken ct) =>
        Task.WhenAll(WatchPhpDeploymentsAsync(ns, changed, ct), WatchPhpServicesAsync(ns, changed, ct),
            WatchPhpConfigMapsAsync(ns, changed, ct), WatchPhpPoliciesAsync(ns, changed, ct));

    private Task WatchPhpDeploymentsAsync(string ns, Action changed, CancellationToken ct)
    {
        const string selector = "hostingpanel/component=account-php";
        var key = ns + "/php-deployments";
        return RunWatchAsync<V1Deployment>(
            async token => KubernetesJson.Serialize(await Api.AppsV1.ListNamespacedDeploymentAsync(ns, labelSelector: selector, cancellationToken: token)),
            (rv, token) => Api.AppsV1.ListNamespacedDeploymentWithHttpMessagesAsync(ns, labelSelector: selector,
                resourceVersion: rv, allowWatchBookmarks: true, watch: true, timeoutSeconds: 300, cancellationToken: token)
                .WatchAsync<V1Deployment, V1DeploymentList>(onError: e => throw new InvalidOperationException("PHP watch failed.", e), cancellationToken: token),
            json => ObserveList(key, json, _ => true, changed),
            (eventType, item) => ObserveEvent(key, eventType, item, _ => true, changed), ct);
    }

    private Task WatchPhpServicesAsync(string ns, Action changed, CancellationToken ct)
    {
        const string selector = "hostingpanel/component=account-php";
        var key = ns + "/php-services";
        return RunWatchAsync<V1Service>(
            async token => KubernetesJson.Serialize(await Api.CoreV1.ListNamespacedServiceAsync(ns, labelSelector: selector, cancellationToken: token)),
            (rv, token) => Api.CoreV1.ListNamespacedServiceWithHttpMessagesAsync(ns, labelSelector: selector,
                resourceVersion: rv, allowWatchBookmarks: true, watch: true, timeoutSeconds: 300, cancellationToken: token)
                .WatchAsync<V1Service, V1ServiceList>(onError: e => throw new InvalidOperationException("PHP watch failed.", e), cancellationToken: token),
            json => ObserveList(key, json, _ => true, changed),
            (eventType, item) => ObserveEvent(key, eventType, item, _ => true, changed), ct);
    }

    private Task WatchPhpConfigMapsAsync(string ns, Action changed, CancellationToken ct)
    {
        const string selector = "hostingpanel/component=account-php";
        var key = ns + "/php-configmaps";
        return RunWatchAsync<V1ConfigMap>(
            async token => KubernetesJson.Serialize(await Api.CoreV1.ListNamespacedConfigMapAsync(ns, labelSelector: selector, cancellationToken: token)),
            (rv, token) => Api.CoreV1.ListNamespacedConfigMapWithHttpMessagesAsync(ns, labelSelector: selector,
                resourceVersion: rv, allowWatchBookmarks: true, watch: true, timeoutSeconds: 300, cancellationToken: token)
                .WatchAsync<V1ConfigMap, V1ConfigMapList>(onError: e => throw new InvalidOperationException("PHP watch failed.", e), cancellationToken: token),
            json => ObserveList(key, json, _ => true, changed),
            (eventType, item) => ObserveEvent(key, eventType, item, _ => true, changed), ct);
    }

    private Task WatchPhpPoliciesAsync(string ns, Action changed, CancellationToken ct)
    {
        const string selector = "hostingpanel/component=account-php";
        var key = ns + "/php-policies";
        return RunWatchAsync<V1NetworkPolicy>(
            async token => KubernetesJson.Serialize(await Api.NetworkingV1.ListNamespacedNetworkPolicyAsync(ns, labelSelector: selector, cancellationToken: token)),
            (rv, token) => Api.NetworkingV1.ListNamespacedNetworkPolicyWithHttpMessagesAsync(ns, labelSelector: selector,
                resourceVersion: rv, allowWatchBookmarks: true, watch: true, timeoutSeconds: 300, cancellationToken: token)
                .WatchAsync<V1NetworkPolicy, V1NetworkPolicyList>(onError: e => throw new InvalidOperationException("PHP watch failed.", e), cancellationToken: token),
            json => ObserveList(key, json, _ => true, changed),
            (eventType, item) => ObserveEvent(key, eventType, item, _ => true, changed), ct);
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
