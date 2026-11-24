using System.Net;
using System.Net.Http.Headers;
using HostingPanel.Api.Adapters;

namespace HostingPanel.Api.Services;

public sealed record VisibleAccount(string Username, string Role, string? Creator);

public sealed record VisibleDomain(string Domain, string Owner, string OwnerRole);

public sealed record AccountCatalogResult(int Status, IReadOnlyList<VisibleAccount>? Accounts, string? Error);
