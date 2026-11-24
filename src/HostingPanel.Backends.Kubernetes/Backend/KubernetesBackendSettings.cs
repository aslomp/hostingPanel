using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace HostingPanel.Api;

public enum KubernetesIngressProvider
{
    [Display(Name = "NGINX Ingress")]
    NginxIngress,
    [Display(Name = "Envoy Proxy (Gateway API)")]
    EnvoyProxy
}

public sealed class KubernetesBackendSettings : IBackendSettings
{
    [Display(Name = "Ingress provider", Description = "Desired routing implementation for hosted domains. Reconciliation is not implemented yet.")]
    public KubernetesIngressProvider IngressProvider { get; set; } = KubernetesIngressProvider.NginxIngress;

    [JsonIgnore]
    public IBackendSettingsValidator Validator { get; } = new KubernetesBackendSettingsValidator();
}

public sealed class KubernetesBackendSettingsValidator : IBackendSettingsValidator
{
    public Dictionary<string, string[]> Validate(IBackendSettings settings)
    {
        if (settings is not KubernetesBackendSettings typed)
            return new() { ["Values"] = ["Wrong settings contract."] };
        return Enum.IsDefined(typed.IngressProvider) ? new() :
            new() { [nameof(typed.IngressProvider)] = ["Select a supported ingress provider."] };
    }
}
