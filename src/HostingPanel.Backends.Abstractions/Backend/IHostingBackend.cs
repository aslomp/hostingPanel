using Microsoft.AspNetCore.Http;
using System.Net.Http.Headers;
using HostingPanel.Api.Adapters;
using HostingPanel.Api.Services;

namespace HostingPanel.Api;

/// <summary>No provider-specific implementations appear in this contract.</summary>
public interface IHostingBackend
{
    IBackendSettingsAccess? Settings => null;
    Task<int> AuthenticateSessionAsync(AuthenticationHeaderValue auth, CancellationToken ct) =>
        Task.FromResult(501);
    IAccountCatalog AccountCatalog { get; }
    IAccountMutations AccountMutations { get; }
    IAccessibleDomains AccessibleDomains { get; }
    IUserDetails UserDetails { get; }
    IAccountOverview AccountOverview { get; }
    IImpersonation Impersonation { get; }
    IDatabases Databases { get; }
    IAccessibleDatabases AccessibleDatabases { get; }
    IDomains Domains { get; }
    IDns Dns { get; }
    IFileAccess FileAccess { get; }
    ITickets Tickets { get; }
    Task ForwardAsync(HttpContext context);
    IResult PreviewDomain(DomainCreatePreview input);
}

public interface IAccountCatalog {
    Task<AccountCatalogResult> ListAsync(AuthenticationHeaderValue auth, CancellationToken ct);
}
public interface IAccountMutations {
    Task<AccountOperationResult> CreateAsync(CreateHostingAccount input, AuthenticationHeaderValue auth, CancellationToken ct);
    Task<AccountOperationResult> DeleteAsync(string username, string confirmation, AuthenticationHeaderValue auth, CancellationToken ct);
    Task<AccountOperationResult> SetPackageAsync(string username, string package, AuthenticationHeaderValue auth, CancellationToken ct);
    Task<AccountOperationResult> SetLimitAsync(string kind, string username, AccountLimitChange change, AuthenticationHeaderValue auth, CancellationToken ct);
    Task<AccountOperationResult> SetSuspendedAsync(string username, bool suspended, AuthenticationHeaderValue auth, CancellationToken ct);
}
public interface IAccessibleDomains {
    Task<VisibleDomainsResult> ListAsync(AuthenticationHeaderValue auth, CancellationToken ct);
}
public interface IUserDetails {
    Task<UserDetailResult> GetAsync(string username, string kind, AuthenticationHeaderValue auth, CancellationToken ct);
}
public interface IAccountOverview {
    Task<AccountOverviewResult> GetAsync(string username, AuthenticationHeaderValue auth, CancellationToken ct);
}
public interface IImpersonation {
    Task<ImpersonationCheck> CheckAsync(AuthenticationHeaderValue auth, string target, CancellationToken ct);
}
public interface IDatabases {
    Task<DatabaseInventory> ListAsync(AuthenticationHeaderValue auth, CancellationToken ct);
    Task<DatabaseMembers> UsersAsync(string database, AuthenticationHeaderValue auth, CancellationToken ct);
    Task<DatabaseMutation> CreateAsync(DatabaseCreateRequest input, AuthenticationHeaderValue auth, CancellationToken ct);
    Task<DatabaseMutation> DeleteAsync(string database, string confirmation, AuthenticationHeaderValue auth, CancellationToken ct);
}
public interface IAccessibleDatabases {
    Task<AccessibleDatabaseInventory> ListAsync(AuthenticationHeaderValue auth, CancellationToken ct);
}
public interface IDomains {
    Task<DomainListResult> ListAsync(string? owner, AuthenticationHeaderValue auth, CancellationToken ct);
    Task<DomainMutationResult> MutateAsync(DomainMutationRequest input, AuthenticationHeaderValue auth, CancellationToken ct);
    Task<DomainDetailResult> GetDetailsAsync(string domain, AuthenticationHeaderValue auth, CancellationToken ct);
    Task<DomainMutationResult> UpdateSettingAsync(string domain, string setting, string value, AuthenticationHeaderValue auth, CancellationToken ct);
}
public interface IDns {
    Task<AdminDnsZoneListResult> ListAdminZonesAsync(AuthenticationHeaderValue auth, int page, CancellationToken ct);
    Task<DnsZoneResult> ReadAsync(string domain, AuthenticationHeaderValue auth, CancellationToken ct);
    Task<DnsChangeResult> ChangeAsync(string domain, DnsChange change, AuthenticationHeaderValue auth, CancellationToken ct);
    Task<DnsChangeResult> CreateZoneAsync(DnsZoneCreate input, AuthenticationHeaderValue auth, CancellationToken ct);
    Task<DnsChangeResult> DeleteZoneAsync(string domain, string confirmation, AuthenticationHeaderValue auth, CancellationToken ct);
}
public interface IFileAccess {
    Task<FileAccessResult> ListFtpAsync(string domain, AuthenticationHeaderValue credentials, CancellationToken ct);
    Task<FileAccessResult> GetFtpAsync(string domain, string user, AuthenticationHeaderValue credentials, CancellationToken ct);
    Task<FileAccessResult> CreateFtpAsync(FtpAccountRequest input, AuthenticationHeaderValue credentials, CancellationToken ct);
    Task<FileAccessResult> UpdateFtpAsync(string user, FtpAccountRequest input, AuthenticationHeaderValue credentials, CancellationToken ct);
    Task<FileAccessResult> DeleteFtpAsync(string domain, string user, AuthenticationHeaderValue credentials, CancellationToken ct);
}

public interface ITickets
{
    Task<(int Status, TicketInbox? Inbox, string? Error)> GetInboxAsync(AuthenticationHeaderValue auth, CancellationToken ct);
    Task<TicketActionResult> CreateAsync(TicketCreateRequest input, AuthenticationHeaderValue auth, CancellationToken ct);
    Task<TicketActionResult> ReplyAsync(string legacyId, TicketReplyRequest input, AuthenticationHeaderValue auth, CancellationToken ct);
    Task<TicketActionResult> SetStatusAsync(string legacyId, TicketStatusChange input, AuthenticationHeaderValue auth, CancellationToken ct);
}
