using HostingPanel.Providers.PowerDNS;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HostingPanel.Api.Adapters;
using HostingPanel.Api.Persistence;
using HostingPanel.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace HostingPanel.Api;

public sealed class KubernetesAccountService(HostingPanelDbContext db, KubernetesIdentityService identity, KubernetesProvisioningService provisioning, KubernetesOperationQueue operations) : IAccountCatalog, IAccountMutations, IAccountOverview, IUserDetails, IImpersonation
{
    public async Task<AccountCatalogResult> ListAsync(AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var principal = await identity.AuthenticateAsync(auth, ct);
        if (principal is null) return new(401, null, "UNAUTHORIZED");
        var ids = await identity.VisibleAccountsAsync(principal, ct);
        var items = await db.Accounts.AsNoTracking().Where(a => ids.Contains(a.Id))
            .OrderBy(a => a.Username).ToListAsync(ct);
        var names = items.ToDictionary(a => a.Id, a => a.Username);
        return new(200, items.Select(a => new VisibleAccount(a.Username, a.Kind,
            a.CreatorId is { } id && names.TryGetValue(id, out var creator) ? creator : null)).ToArray(), null);
    }

    public async Task<AccountOperationResult> CreateAsync(CreateHostingAccount input, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var principal = await identity.AuthenticateAsync(auth, ct);
        if (principal is null) return new(401, "UNAUTHORIZED");
        if (input.Kind is not ("user" or "reseller") || !KubernetesInputValidation.ValidUser(input.Username)
            || !KubernetesInputValidation.ValidDomain(input.Domain) || string.IsNullOrWhiteSpace(input.Email)
            || input.Password.Length < 12 || input.Password.Length > 256)
            return new(400, "INVALID_ACCOUNT_REQUEST");
        if (principal.Kind is not ("admin" or "reseller")) return new(403, "ACCESS_DENIED");
        if (principal.Kind == "reseller" && input.Kind != "user") return new(403, "ACCESS_DENIED");
        if (await db.Accounts.AnyAsync(a => a.Username == input.Username, ct)) return new(409, "ACCOUNT_EXISTS");
        var package = await db.Packages.FirstOrDefaultAsync(p => p.Name == input.Package
            && (p.OwnerId == principal.Id || p.OwnerId == null), ct);
        if (package is null) return new(404, "PACKAGE_NOT_FOUND");
        if (await db.Configuration.AnyAsync(x => x.Key == "DeletingDomain:" + input.Domain.ToLowerInvariant(), ct)) return new(409, "DOMAIN_DELETION_PENDING");
        if (await db.Domains.AnyAsync(d => d.Name == input.Domain.ToLowerInvariant(), ct))
            return new(409, "DOMAIN_EXISTS");
        var account = new HostingAccount {
            Username = input.Username, Kind = input.Kind, Email = input.Email,
            CreatorId = principal.Id, PackageId = package.Id,
            PasswordHash = PasswordVerifier.Hash(input.Password)
        };
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        db.Accounts.Add(account);
        var domain = new HostedDomain { Name = input.Domain.ToLowerInvariant(), OwnerId = account.Id };
        db.Domains.Add(domain);
        if (await provisioning.StageDomainDnsAsync(domain, ct) is { } dnsError)
        {
                if (dnsError == "DNS_ZONE_EXISTS") return new(409, dnsError);
                db.ReconciliationOperations.Add(new ReconciliationOperation { ResourceType = "domain-dns", ResourceId = domain.Id, Action = "create", LastError = dnsError });
            }
        operations.Schedule("account", account.Id, "create", new { account.Username });
        operations.Schedule("domain", domain.Id, "create", new { domain.Name });
        await provisioning.ScheduleWebsiteAsync(domain, "create", ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(202, null);
    }

    public async Task<AccountOperationResult> DeleteAsync(string username, string confirmation, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var principal = await identity.AuthenticateAsync(auth, ct);
        if (principal is null) return new(401, "UNAUTHORIZED");
        if (!KubernetesInputValidation.ValidUser(username) || username != confirmation) return new(400, "CONFIRMATION_REQUIRED");
        var ids = await identity.VisibleAccountsAsync(principal, ct);
        var target = await db.Accounts.FirstOrDefaultAsync(x => x.Username == username, ct);
        if (target is null) return new(404, "ACCOUNT_NOT_FOUND");
        if (target.Id == principal.Id || !ids.Contains(target.Id)) return new(403, "ACCESS_DENIED");
        // Deletion is blocked when dependent resources still exist: never silently orphan workloads.
        if (await db.Accounts.AnyAsync(x => x.CreatorId == target.Id, ct)
            || await db.Domains.AnyAsync(x => x.OwnerId == target.Id, ct)
            || await db.DnsZones.AnyAsync(x => x.OwnerId == target.Id, ct)
            || await db.Databases.AnyAsync(x => x.OwnerId == target.Id, ct)
            || await db.DatabaseUsers.AnyAsync(x => x.OwnerId == target.Id, ct)
            || await db.FileAccessAccounts.AnyAsync(x => x.OwnerId == target.Id, ct))
            return new(409, "ACCOUNT_HAS_RESOURCES");
        var nameservers = await db.Configuration.SingleOrDefaultAsync(x => x.Key == KubernetesAccountNameservers.Key(target.Id), ct);
        if (nameservers is not null) db.Configuration.Remove(nameservers);
        db.Accounts.Remove(target);
        operations.Schedule("account", target.Id, "delete", new { target.Username });
        await db.SaveChangesAsync(ct);
        return new(202, null);
    }

    public async Task<AccountOperationResult> SetPackageAsync(string username, string package, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var principal = await identity.AuthenticateAsync(auth, ct);
        if (principal is null) return new(401, "UNAUTHORIZED");
        var ids = await identity.VisibleAccountsAsync(principal, ct);
        var account = await db.Accounts.FirstOrDefaultAsync(x => x.Username == username, ct);
        if (account is null) return new(404, "ACCOUNT_NOT_FOUND");
        if (!ids.Contains(account.Id) || account.Id == principal.Id) return new(403, "ACCESS_DENIED");
        var assigned = await db.Packages.FirstOrDefaultAsync(x => x.Name == package
            && (x.OwnerId == principal.Id || x.OwnerId == null), ct);
        if (assigned is null) return new(404, "PACKAGE_NOT_FOUND");
        account.PackageId = assigned.Id;
        operations.Schedule("account", account.Id, "update", new { account.Username, package });
        await db.SaveChangesAsync(ct);
        return new(202, null);
    }

    public async Task<AccountOperationResult> SetLimitAsync(string kind, string username, AccountLimitChange change,
        AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var principal = await identity.AuthenticateAsync(auth, ct);
        if (principal is null) return new(401, "UNAUTHORIZED");
        if (change.Field is not ("bandwidth" or "quota" or "inode")
            || !long.TryParse(change.Value, out var amount) || amount < 0) return new(400, "INVALID_LIMIT");
        var ids = await identity.VisibleAccountsAsync(principal, ct);
        var account = await db.Accounts.FirstOrDefaultAsync(x => x.Username == username && x.Kind == kind, ct);
        if (account is null) return new(404, "ACCOUNT_NOT_FOUND");
        if (!ids.Contains(account.Id) || account.Id == principal.Id) return new(403, "ACCESS_DENIED");
        var limits = JsonSerializer.Deserialize<Dictionary<string, string>>(account.LimitsJson ?? "{}") ?? new();
        limits[change.Field] = change.Value;
        account.LimitsJson = JsonSerializer.Serialize(limits);
        operations.Schedule("account", account.Id, "update", new { account.Username, change.Field });
        await db.SaveChangesAsync(ct);
        return new(202, null);
    }

    public async Task<AccountOperationResult> SetSuspendedAsync(string username, bool suspended,
        AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var principal = await identity.AuthenticateAsync(auth, ct);
        if (principal is null) return new(401, "UNAUTHORIZED");
        var ids = await identity.VisibleAccountsAsync(principal, ct);
        var target = await db.Accounts.FirstOrDefaultAsync(a => a.Username == username, ct);
        if (target is null) return new(404, "ACCOUNT_NOT_FOUND");
        if (target.Id == principal.Id || !ids.Contains(target.Id)) return new(403, "ACCESS_DENIED");
        target.Suspended = suspended;
        operations.Schedule("account", target.Id, "update", new { target.Username, suspended });
        await db.SaveChangesAsync(ct);
        return new(202, null);
    }
    public async Task<AccountOverviewResult> GetAsync(string username, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var principal = await identity.AuthenticateAsync(auth, ct);
        if (principal is null) return new(401, null, null, null, "UNAUTHORIZED");
        var ids = await identity.VisibleAccountsAsync(principal, ct);
        var row = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Username == username, ct);
        if (row is null) return new(404, null, null, null, "ACCOUNT_NOT_FOUND");
        if (!ids.Contains(row.Id)) return new(403, null, null, null, "ACCESS_DENIED");
        var package = row.PackageId is null ? null :
            await db.Packages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == row.PackageId, ct);
        var config = new Dictionary<string, string> {
            ["username"] = row.Username, ["usertype"] = row.Kind, ["email"] = row.Email,
            ["suspended"] = row.Suspended ? "yes" : "no",
            ["package"] = package?.Name ?? ""
        };
        var limits = package is null ? null :
            JsonSerializer.Deserialize<Dictionary<string, string>>(package.LimitsJson);
        return new(200, config, package?.Name, limits, null);
    }

    public Task<UserDetailResult> GetAsync(string username, string kind, AuthenticationHeaderValue auth, CancellationToken ct)
        => Task.FromResult(new UserDetailResult(501, null, "KUBERNETES_MODERN_USER_SCHEMA_NOT_IMPLEMENTED"));

    public Task<ImpersonationCheck> CheckAsync(AuthenticationHeaderValue auth, string target, CancellationToken ct)
        => Task.FromResult(new ImpersonationCheck(501, "KUBERNETES_IMPERSONATION_NOT_IMPLEMENTED"));
}
