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

    public async Task<string> ListPodsAsync(string ns, string selector, CancellationToken ct) =>
        KubernetesJson.Serialize(await Api.CoreV1.ListNamespacedPodAsync(ns, labelSelector: selector, cancellationToken: ct));

    public void Dispose() { if (client.IsValueCreated) client.Value.Dispose(); }
}
