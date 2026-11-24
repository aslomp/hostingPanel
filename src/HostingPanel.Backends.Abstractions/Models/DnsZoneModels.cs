using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using HostingPanel.Api.Adapters;

namespace HostingPanel.Api.Services;

public sealed record DnsZoneResult(int Status, IReadOnlyList<DnsRecord>? Records, string? Error);

public sealed record DnsChangeResult(int Status, string? Error)
{
    public bool Executed => Status == 200 && Error is null;
    public bool ProvisioningPending => Status == 202 && Error is null;
}

public sealed record AccessibleDnsZone(string Domain, bool? LocalData, bool? LocalMail, string? Owner);
public sealed record AccessibleDnsInventory(int Status, IReadOnlyList<AccessibleDnsZone>? Zones, string? Warning);

public sealed record AdminDnsZoneListResult(int Status, IReadOnlyList<AdminDnsZone>? Zones, string? Error);

public sealed record DnsZoneCreate(string Domain, string Ip, string Ns1, string Ns2);
