using System.Net.Http.Headers;
using HostingPanel.Api.Adapters;

namespace HostingPanel.Api.Services;

public sealed record VisibleDomainsResult(int Status,
    IReadOnlyList<VisibleDomain>? Items, string? Warning);
