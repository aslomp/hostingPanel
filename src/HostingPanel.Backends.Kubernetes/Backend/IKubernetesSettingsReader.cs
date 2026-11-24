using HostingPanel.Api.Persistence;

namespace HostingPanel.Api;

internal interface IKubernetesSettingsReader
{
    Task<KubernetesBackendSettings> GetEffectiveSettingsAsync(CancellationToken ct);
}

internal interface IDomainDnsStaging
{
    Task<string?> StageDomainDnsAsync(HostedDomain domain, CancellationToken ct, bool retry = false);
}
