using System.Net;
using System.Net.Http.Headers;
using HostingPanel.Api.Adapters;

namespace HostingPanel.Api.Services;

public sealed record CreateHostingAccount(
    string Kind, string Username, string Email, string Password,
    string Domain, string Package, string Ip = "shared", bool Notify = false);

public sealed record AccountDeletion(string Confirmation);

public sealed record AccountLimitChange(string Field, string Value);

public sealed record AccountSuspension(bool Suspended);

public sealed record AccountOperationResult(int Status, string? Error);

// Backend-authoritative packages available to the authenticated account creator.
public interface IAccountPackages
{
    Task<AccountPackagesResult> ListAsync(string kind, AuthenticationHeaderValue auth, CancellationToken ct);
}

public sealed record AccountPackagesResult(int Status, IReadOnlyList<string>? Packages, string? Error);
