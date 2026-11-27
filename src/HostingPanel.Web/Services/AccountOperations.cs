using HostingPanel.Api;
using HostingPanel.Api.Services;

namespace HostingPanel.Web.Services;

public sealed class AccountOperations(BackendOperationScope scope)
{
    public Task<PhpRuntimeView> PhpAsync(string username, CancellationToken ct = default) =>
        scope.RunOptionalAsync<IAccountPhpSettings, PhpRuntimeView>((p, auth, token) => p.ReadAsync(username, auth, token), new(501, null, []), ct);

    public Task<PhpRuntimeView> PhpDefaultsAsync(CancellationToken ct = default) =>
        scope.RunOptionalAsync<IAccountPhpSettings, PhpRuntimeView>((p, auth, token) => p.DefaultsAsync(auth, token), new(501, null, []), ct);

    public Task<AccountOperationResult> SavePhpAsync(string username, PhpRuntimeSettings? settings, CancellationToken ct = default) =>
        scope.RunOptionalAsync<IAccountPhpSettings, AccountOperationResult>((p, auth, token) => p.WriteAsync(username, settings, auth, token), new(501, "UNSUPPORTED"), ct);

    public Task<AccountOverviewResult> OverviewAsync(string username, CancellationToken ct = default) =>
        scope.RunAsync<IAccountOverview, AccountOverviewResult>((p, auth, token) => p.GetAsync(username, auth, token), ct);

    public Task<AccountCatalogResult> ListAsync(CancellationToken ct = default) =>
        scope.RunAsync<IAccountCatalog, AccountCatalogResult>((p, auth, token) => p.ListAsync(auth, token), ct);

    public Task<AccountPackagesResult> ListPackagesAsync(string kind, CancellationToken ct = default) =>
        scope.RunAsync<IAccountPackages, AccountPackagesResult>((p, auth, token) => p.ListAsync(kind, auth, token), ct);

    public Task<AccountOperationResult> CreateAsync(CreateHostingAccount input, CancellationToken ct = default) =>
        scope.RunAsync<IAccountMutations, AccountOperationResult>((p, auth, token) => p.CreateAsync(input, auth, token), ct);

    public Task<AccountOperationResult> DeleteAsync(string username, string confirmation, CancellationToken ct = default) =>
        scope.RunAsync<IAccountMutations, AccountOperationResult>((p, auth, token) => p.DeleteAsync(username, confirmation, auth, token), ct);

    public Task<AccountOperationResult> SetPackageAsync(string username, string package, CancellationToken ct = default) =>
        scope.RunAsync<IAccountMutations, AccountOperationResult>((p, auth, token) => p.SetPackageAsync(username, package, auth, token), ct);

    public Task<AccountOperationResult> SetLimitAsync(string kind, string username, string field, string value, CancellationToken ct = default) =>
        scope.RunAsync<IAccountMutations, AccountOperationResult>((p, auth, token) => p.SetLimitAsync(kind, username, new AccountLimitChange(field, value), auth, token), ct);

    public Task<AccountOperationResult> SetSuspendedAsync(string username, bool suspended, CancellationToken ct = default) =>
        scope.RunAsync<IAccountMutations, AccountOperationResult>((p, auth, token) => p.SetSuspendedAsync(username, suspended, auth, token), ct);
}
