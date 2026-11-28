using HostingPanel.Api;
using HostingPanel.Api.Services;

namespace HostingPanel.Web.Services;

public sealed class DomainOperations(BackendOperationScope scope)
{
    public Task<VisibleDomainsResult> ListAccessibleAsync(CancellationToken ct = default) =>
        scope.RunAsync<IAccessibleDomains, VisibleDomainsResult>((p, auth, token) => p.ListAsync(auth, token), ct);

    public Task<DomainListResult> ListAsync(string? owner, CancellationToken ct = default) =>
        scope.RunAsync<IDomains, DomainListResult>((p, auth, token) => p.ListAsync(owner, auth, token), ct);

    public Task<DomainDetailResult> GetAsync(string domain, CancellationToken ct = default) =>
        scope.RunAsync<IDomains, DomainDetailResult>((p, auth, token) => p.GetDetailsAsync(domain, auth, token), ct);

    public Task<DomainMutationResult> MutateAsync(DomainMutationRequest input, CancellationToken ct = default) =>
        scope.RunAsync<IDomains, DomainMutationResult>((p, auth, token) => p.MutateAsync(input, auth, token), ct);

    public Task<DomainMutationResult> UpdateSettingAsync(string domain, string setting, string value, CancellationToken ct = default) =>
        scope.RunAsync<IDomains, DomainMutationResult>((p, auth, token) => p.UpdateSettingAsync(domain, setting, value, auth, token), ct);

    public Task<SubdomainInventory> ListSubdomainsAsync(string domain, CancellationToken ct = default) =>
        scope.RunOptionalAsync<ISubdomains, SubdomainInventory>((p, auth, token) => p.ListAsync(domain, auth, token),
            new(501, null, "SUBDOMAINS_UNSUPPORTED"), ct);

    public Task<SubdomainMutation> CreateSubdomainAsync(string domain, string label, CancellationToken ct = default) =>
        scope.RunOptionalAsync<ISubdomains, SubdomainMutation>((p, auth, token) => p.CreateAsync(domain, label, auth, token),
            new(501, "SUBDOMAINS_UNSUPPORTED"), ct);

    public Task<SubdomainMutation> DeleteSubdomainAsync(string domain, string label, string confirmation, CancellationToken ct = default) =>
        scope.RunOptionalAsync<ISubdomains, SubdomainMutation>((p, auth, token) => p.DeleteAsync(domain, label, confirmation, auth, token),
            new(501, "SUBDOMAINS_UNSUPPORTED"), ct);

    public Task<DomainLogResult> ReadLogsAsync(string domain,string kind,long? before,int count,CancellationToken ct = default) =>
        scope.RunOptionalAsync<IDomainLogs,DomainLogResult>((p,a,token) => p.ReadAsync(domain,kind,before,count,a,token),new(501,"DOMAIN_LOGS_NOT_SUPPORTED"),ct);

    public async Task<string?> GetProvisioningStatusAsync(string domain, CancellationToken ct = default)
    {
        var result = await GetAsync(domain, ct);
        return result.Status == 200 && result.Settings?.TryGetValue("provisioning", out var status) == true ? status : null;
    }
}
