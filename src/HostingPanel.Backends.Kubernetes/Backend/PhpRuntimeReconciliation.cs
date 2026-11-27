using HostingPanel.Providers.CertManager;

namespace HostingPanel.Api;

// Serialized by WebsiteReconciler. Apply only changed manifests; retirement
// remains behind successful Apache activation.
internal sealed class PhpRuntimeReconciliation(IConfiguration configuration, KubernetesResourceClient resources)
{
    private readonly Dictionary<string, string> applied = new(StringComparer.Ordinal);
    private HashSet<string>? retiredAgainst;

    internal async Task<HashSet<string>> EnsureAsync(string ns,
        IEnumerable<(string Username, PhpAccountIdentity Identity, PhpRuntimeSettings Settings)> sites, CancellationToken ct)
    {
        var runtime = new AccountPhpRuntime(configuration, resources, ns);
        var desired = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (username, identity, settings) in sites.DistinctBy(x => x.Identity.AccountId))
        {
            var name = AccountPhpRuntime.Name(identity.AccountId);
            var manifest = runtime.BuildManifest(username, identity, settings);
            if (!applied.TryGetValue(name, out var previous) || previous != manifest)
            {
                await resources.ApplyAsync(manifest, ns, "hostingpanel", ct);
                applied[name] = manifest;
            }
            desired.Add(name);
        }
        return desired;
    }

    internal void InvalidateAll() { applied.Clear(); retiredAgainst = null; }

    internal async Task RetireAsync(string ns, IReadOnlySet<string> desired, CancellationToken ct)
    {
        if (retiredAgainst is not null && retiredAgainst.SetEquals(desired)) return;
        await resources.RetireAccountPhpResourcesAsync(ns, desired, ct);
        retiredAgainst = new HashSet<string>(desired, StringComparer.Ordinal);
        foreach (var name in applied.Keys.Where(name => !desired.Contains(name)).ToArray()) applied.Remove(name);
    }
}
