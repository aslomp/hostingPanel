using System.Net.Http.Headers;
using HostingPanel.Api.Adapters;
using HostingPanel.Api.Services;

namespace HostingPanel.Api;

/// <summary>Authoritative DNS desired-state management, independent of the DNS server,
/// registrar, hosting runtime or transport. Providers own authorization and provisioning.</summary>
public interface IDNSProvider
{
    Task<AccessibleDnsInventory> ListAccessibleZonesAsync(AuthenticationHeaderValue auth, CancellationToken ct);
    Task<AdminDnsZoneListResult> ListAdminZonesAsync(AuthenticationHeaderValue auth, int page, CancellationToken ct);
    Task<DnsZoneResult> ReadAsync(string domain, AuthenticationHeaderValue auth, CancellationToken ct);
    Task<DnsChangeResult> ChangeAsync(string domain, DnsChange change, AuthenticationHeaderValue auth, CancellationToken ct);
    Task<DnsChangeResult> CreateZoneAsync(DnsZoneCreate input, AuthenticationHeaderValue auth, CancellationToken ct);
    Task<DnsChangeResult> DeleteZoneAsync(string domain, string confirmation, AuthenticationHeaderValue auth, CancellationToken ct);

    Task<DnsChangeResult> CreateRecordAsync(string zone, DnsRecord record, AuthenticationHeaderValue auth, CancellationToken ct)
        => ChangeAsync(zone, new("add", record), auth, ct);
    Task<DnsChangeResult> UpdateRecordAsync(string zone, DnsRecord previous, DnsRecord record, AuthenticationHeaderValue auth, CancellationToken ct)
        => ChangeAsync(zone, new("edit", record, previous), auth, ct);
    Task<DnsChangeResult> DeleteRecordAsync(string zone, DnsRecord record, AuthenticationHeaderValue auth, CancellationToken ct)
        => ChangeAsync(zone, new("delete", record, record), auth, ct);
}
