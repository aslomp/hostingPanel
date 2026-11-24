using HostingPanel.Api.Services;

namespace HostingPanel.Web.Services;

public sealed class PackageOperations(BackendOperationScope scope)
{
    public Task<AccountPackagesResult> ListAsync(string kind, CancellationToken ct = default) =>
        scope.RunAsync<IPackageManagement, AccountPackagesResult>((p,a,t) => p.ListAsync(kind,a,t),ct);
    public Task<PackageDetailsResult> GetAsync(string kind, string name, CancellationToken ct = default) =>
        scope.RunAsync<IPackageManagement, PackageDetailsResult>((p,a,t) => p.GetAsync(kind,name,a,t),ct);
    public Task<AccountOperationResult> SaveAsync(string kind, HostingPackageDefinition package, bool create, CancellationToken ct = default) =>
        scope.RunAsync<IPackageManagement, AccountOperationResult>((p,a,t) => p.SaveAsync(kind,package,create,a,t),ct);
    public Task<AccountOperationResult> DeleteAsync(string kind, string name, CancellationToken ct = default) =>
        scope.RunAsync<IPackageManagement, AccountOperationResult>((p,a,t) => p.DeleteAsync(kind,name,a,t),ct);
}
