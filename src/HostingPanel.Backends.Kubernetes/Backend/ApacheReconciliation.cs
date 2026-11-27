using HostingPanel.Api.Persistence;
using HostingPanel.Providers.CertManager;
using Microsoft.Extensions.Logging;

namespace HostingPanel.Api;

// Serialized by WebsiteReconciler; activation is the barrier before retirement.
internal sealed class ApacheReconciliation(IConfiguration configuration, KubernetesResourceClient resources, ILogger logger)
{
    private string? stagedApacheRevision;
    private string Namespace => configuration["KubernetesHosting:Namespace"] ?? "hosting";
    private string Required(string key) => configuration["KubernetesHosting:" + key] is { Length: > 0 } value
        ? value : throw new InvalidOperationException("Missing KubernetesHosting:" + key);

    internal void Invalidate() => stagedApacheRevision = null;

    internal async Task ActivateAsync(
        IReadOnlyList<HostedDomain> domains,
        IReadOnlyDictionary<Guid, HostingAccount> accounts,
        IReadOnlyDictionary<Guid, PhpAccountIdentity> identities,
        IReadOnlyDictionary<Guid, Exception> storageErrors,
        CancellationToken ct)
    {
        if (storageErrors.Count != 0)
            throw new InvalidOperationException("Website storage unavailable; retaining previous Apache revision.");
        var revision = new ApacheConfigurationRevision(domains.Select(domain =>
            KeyValuePair.Create(domain.Id, WebsiteReconciler.BuildVirtualHost(domain,
                accounts[domain.OwnerId].Username, identities[domain.OwnerId]).Apache)));
        if (revision.Id == stagedApacheRevision) return;
        var store = new ApacheCandidateStore(Required("ApacheConfigPath"));
        var runtime = new ApacheRevisionRuntime(resources, Namespace);
        var fingerprint = await runtime.GetReplicaFingerprintAsync(ct);
        await new ApacheRevisionActivator(store).ActivateAsync(revision,
            runtime.ValidateAsync, runtime.ReloadAsync, ct);
        if (fingerprint != await runtime.GetReplicaFingerprintAsync(ct))
            throw new InvalidOperationException("Apache replica set changed during activation; retry required.");
        stagedApacheRevision = revision.Id;
        logger.LogInformation("Activated Apache configuration revision {Revision} with {Count} virtualhosts",
            revision.Id, revision.VirtualHosts.Count);
    }


}
