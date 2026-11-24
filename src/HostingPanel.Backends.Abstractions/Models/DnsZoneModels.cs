using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using HostingPanel.Api.Adapters;

namespace HostingPanel.Api.Services;

public sealed record DnsZoneResult(int Status, IReadOnlyList<DnsRecord>? Records, string? Error);

public sealed record DnsChangeResult(int Status, string? Error);

public sealed record AdminDnsZoneListResult(int Status, IReadOnlyList<AdminDnsZone>? Zones, string? Error);

public sealed record DnsZoneCreate(string Domain, string Ip, string Ns1, string Ns2);
