using HostingPanel.Api;
using HostingPanel.Api.Adapters;
using HostingPanel.Api.Services;

namespace HostingPanel.Web.Services;

public sealed class DnsOperations(BackendOperationScope scope)
{
    public Task<AccessibleDnsInventory> ListAsync(CancellationToken ct = default) =>
        scope.RunAsync<IDNSProvider, AccessibleDnsInventory>((p, auth, token) => p.ListAccessibleZonesAsync(auth, token), ct);

    public Task<DnsZoneResult> ReadAsync(string domain, CancellationToken ct = default) =>
        scope.RunAsync<IDNSProvider, DnsZoneResult>((p, auth, token) => p.ReadAsync(domain, auth, token), ct);

    public Task<DnsChangeResult> ChangeAsync(string domain, DnsChange change, CancellationToken ct = default) =>
        scope.RunAsync<IDNSProvider, DnsChangeResult>((p, auth, token) => p.ChangeAsync(domain, change, auth, token), ct);

    public Task<DnsChangeResult> CreateAsync(DnsZoneCreate input, CancellationToken ct = default) =>
        scope.RunAsync<IDNSProvider, DnsChangeResult>((p, auth, token) => p.CreateZoneAsync(input, auth, token), ct);

    public Task<DnsChangeResult> DeleteAsync(string domain, string confirmation, CancellationToken ct = default) =>
        scope.RunAsync<IDNSProvider, DnsChangeResult>((p, auth, token) => p.DeleteZoneAsync(domain, confirmation, auth, token), ct);
}
