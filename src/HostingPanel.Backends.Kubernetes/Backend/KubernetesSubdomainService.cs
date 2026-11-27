using System.Net.Http.Headers;

namespace HostingPanel.Api;

// Ownership/inventory stage only. Mutations must wait for atomic DNS and
// website provisioning; never report success for a partially staged site.
public sealed class KubernetesSubdomainService(SubdomainOwnership ownership) : ISubdomains
{
    public async Task<SubdomainInventory> ListAsync(string domain, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var (status, parent, error) = await ownership.ResolveAsync(domain, auth, ct);
        return parent is null ? new(status, null, error)
            : new(200, await ownership.ListLabelsAsync(parent, ct), null);
    }

    public Task<SubdomainMutation> CreateAsync(string domain, string label, AuthenticationHeaderValue auth, CancellationToken ct)
        => ValidateAsync(domain, label, null, false, auth, ct);

    public Task<SubdomainMutation> DeleteAsync(string domain, string label, string confirmation, AuthenticationHeaderValue auth, CancellationToken ct)
        => ValidateAsync(domain, label, confirmation, true, auth, ct);

    private async Task<SubdomainMutation> ValidateAsync(string domain, string label, string? confirmation,
        bool deleting, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var (status, parent, error) = await ownership.ResolveAsync(domain, auth, ct);
        if (parent is null) return new(status, error);
        if (!SubdomainNames.ValidLabel(label) || !SubdomainNames.ValidDomain(label + "." + parent.Name))
            return new(400, "INVALID_SUBDOMAIN");
        if (deleting && confirmation != label.ToLowerInvariant() + "." + parent.Name)
            return new(400, "CONFIRMATION_REQUIRED");
        return new(501, "SUBDOMAIN_PROVISIONING_NOT_IMPLEMENTED");
    }
}
