using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HostingPanel.Api.Adapters;
using HostingPanel.Api.Persistence;
using HostingPanel.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace HostingPanel.Api;

/// <summary>
/// SQLite-backed desired state for the Kubernetes provider. No DirectAdmin transport,
/// no legacy command calls and no implicit fallback to DirectAdmin.
/// Reconciliation with the cluster is deliberately a separate responsibility.
/// </summary>
public sealed class KubernetesBackend(HostingPanelDbContext db) :
    IHostingBackend, IAccountCatalog, IAccountMutations, IAccessibleDomains,
    IUserDetails, IAccountOverview, IImpersonation, IDatabases,
    IAccessibleDatabases, IDomains, IDns, IFileAccess
{
    public IAccountCatalog AccountCatalog => this;
    public IAccountMutations AccountMutations => this;
    public IAccessibleDomains AccessibleDomains => this;
    public IUserDetails UserDetails => this;
    public IAccountOverview AccountOverview => this;
    public IImpersonation Impersonation => this;
    public IDatabases Databases => this;
    public IAccessibleDatabases AccessibleDatabases => this;
    public IDomains Domains => this;
    public IDns Dns => this;
    public IFileAccess FileAccess => this;

    private async Task<HostingAccount?> AuthenticateAsync(AuthenticationHeaderValue auth, CancellationToken ct)
    {
        if (!auth.Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase) || auth.Parameter is null)
            return null;
        try
        {
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(auth.Parameter));
            var separator = raw.IndexOf(':');
            if (separator is < 1 or >= 256) return null;
            var name = raw[..separator];
            var password = raw[(separator + 1)..];
            var account = await db.Accounts.AsNoTracking()
                .FirstOrDefaultAsync(a => a.Username == name && !a.Suspended, ct);
            if (account is null || string.IsNullOrEmpty(account.PasswordHash)) return null;
            return PasswordVerifier.Verify(password, account.PasswordHash) ? account : null;
        }
        catch (FormatException) { return null; }
    }

    // Visibility and identity are backend responsibilities, never inferred by the API.
    private async Task<HashSet<Guid>> VisibleAccountsAsync(HostingAccount principal, CancellationToken ct)
    {
        var accounts = await db.Accounts.AsNoTracking()
            .Select(a => new { a.Id, a.CreatorId }).ToListAsync(ct);
        var visible = new HashSet<Guid> { principal.Id };
        var pending = true;
        while (pending)
        {
            pending = false;
            foreach (var a in accounts)
                if (a.CreatorId.HasValue && visible.Contains(a.CreatorId.Value)
                    && visible.Add(a.Id)) pending = true;
        }
        return visible;
    }

    private static bool ValidUser(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 64
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
    private static bool ValidDomain(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 253
        && Uri.CheckHostName(name) == UriHostNameType.Dns;
    private static bool ValidDb(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 128
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    public async Task<AccountCatalogResult> ListAsync(AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var principal = await AuthenticateAsync(auth, ct);
        if (principal is null) return new(401, null, "UNAUTHORIZED");
        var ids = await VisibleAccountsAsync(principal, ct);
        var items = await db.Accounts.AsNoTracking().Where(a => ids.Contains(a.Id))
            .OrderBy(a => a.Username).ToListAsync(ct);
        var names = items.ToDictionary(a => a.Id, a => a.Username);
        return new(200, items.Select(a => new VisibleAccount(a.Username, a.Kind,
            a.CreatorId is { } id && names.TryGetValue(id, out var creator) ? creator : null)).ToArray(), null);
    }

    public async Task<AccountOperationResult> CreateAsync(CreateHostingAccount input, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var principal = await AuthenticateAsync(auth, ct);
        if (principal is null) return new(401, "UNAUTHORIZED");
        if (input.Kind is not ("user" or "reseller") || !ValidUser(input.Username)
            || !ValidDomain(input.Domain) || string.IsNullOrWhiteSpace(input.Email)
            || input.Password.Length < 12 || input.Password.Length > 256)
            return new(400, "INVALID_ACCOUNT_REQUEST");
        if (principal.Kind is not ("admin" or "reseller")) return new(403, "ACCESS_DENIED");
        if (principal.Kind == "reseller" && input.Kind != "user") return new(403, "ACCESS_DENIED");
        if (await db.Accounts.AnyAsync(a => a.Username == input.Username, ct)) return new(409, "ACCOUNT_EXISTS");
        var package = await db.Packages.FirstOrDefaultAsync(p => p.Name == input.Package
            && (p.OwnerId == principal.Id || p.OwnerId == null), ct);
        if (package is null) return new(404, "PACKAGE_NOT_FOUND");
        if (await db.Domains.AnyAsync(d => d.Name == input.Domain.ToLowerInvariant(), ct))
            return new(409, "DOMAIN_EXISTS");
        var account = new HostingAccount {
            Username = input.Username, Kind = input.Kind, Email = input.Email,
            CreatorId = principal.Id, PackageId = package.Id,
            PasswordHash = PasswordVerifier.Hash(input.Password)
        };
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        db.Accounts.Add(account);
        db.Domains.Add(new HostedDomain { Name = input.Domain.ToLowerInvariant(), OwnerId = account.Id });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(200, null);
    }

    public async Task<AccountOperationResult> DeleteAsync(string username, string confirmation, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var principal = await AuthenticateAsync(auth, ct);
        if (principal is null) return new(401, "UNAUTHORIZED");
        if (!ValidUser(username) || username != confirmation) return new(400, "CONFIRMATION_REQUIRED");
        var ids = await VisibleAccountsAsync(principal, ct);
        var target = await db.Accounts.FirstOrDefaultAsync(x => x.Username == username, ct);
        if (target is null) return new(404, "ACCOUNT_NOT_FOUND");
        if (target.Id == principal.Id || !ids.Contains(target.Id)) return new(403, "ACCESS_DENIED");
        // Deletion is blocked when dependent resources still exist: never silently orphan workloads.
        if (await db.Accounts.AnyAsync(x => x.CreatorId == target.Id, ct)
            || await db.Domains.AnyAsync(x => x.OwnerId == target.Id, ct)
            || await db.Databases.AnyAsync(x => x.OwnerId == target.Id, ct)
            || await db.DatabaseUsers.AnyAsync(x => x.OwnerId == target.Id, ct)
            || await db.FileAccessAccounts.AnyAsync(x => x.OwnerId == target.Id, ct))
            return new(409, "ACCOUNT_HAS_RESOURCES");
        db.Accounts.Remove(target);
        await db.SaveChangesAsync(ct);
        return new(200, null);
    }

    public async Task<AccountOperationResult> SetPackageAsync(string username, string package, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var principal = await AuthenticateAsync(auth, ct);
        if (principal is null) return new(401, "UNAUTHORIZED");
        var ids = await VisibleAccountsAsync(principal, ct);
        var account = await db.Accounts.FirstOrDefaultAsync(x => x.Username == username, ct);
        if (account is null) return new(404, "ACCOUNT_NOT_FOUND");
        if (!ids.Contains(account.Id) || account.Id == principal.Id) return new(403, "ACCESS_DENIED");
        var assigned = await db.Packages.FirstOrDefaultAsync(x => x.Name == package
            && (x.OwnerId == principal.Id || x.OwnerId == null), ct);
        if (assigned is null) return new(404, "PACKAGE_NOT_FOUND");
        account.PackageId = assigned.Id;
        await db.SaveChangesAsync(ct);
        return new(200, null);
    }

    public async Task<AccountOperationResult> SetLimitAsync(string kind, string username, AccountLimitChange change,
        AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var principal = await AuthenticateAsync(auth, ct);
        if (principal is null) return new(401, "UNAUTHORIZED");
        if (change.Field is not ("bandwidth" or "quota" or "inode")
            || !long.TryParse(change.Value, out var amount) || amount < 0) return new(400, "INVALID_LIMIT");
        var ids = await VisibleAccountsAsync(principal, ct);
        var account = await db.Accounts.FirstOrDefaultAsync(x => x.Username == username && x.Kind == kind, ct);
        if (account is null) return new(404, "ACCOUNT_NOT_FOUND");
        if (!ids.Contains(account.Id) || account.Id == principal.Id) return new(403, "ACCESS_DENIED");
        var limits = JsonSerializer.Deserialize<Dictionary<string, string>>(account.LimitsJson ?? "{}") ?? new();
        limits[change.Field] = change.Value;
        account.LimitsJson = JsonSerializer.Serialize(limits);
        await db.SaveChangesAsync(ct);
        return new(200, null);
    }

    public async Task<AccountOperationResult> SetSuspendedAsync(string username, bool suspended,
        AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var principal = await AuthenticateAsync(auth, ct);
        if (principal is null) return new(401, "UNAUTHORIZED");
        var ids = await VisibleAccountsAsync(principal, ct);
        var target = await db.Accounts.FirstOrDefaultAsync(a => a.Username == username, ct);
        if (target is null) return new(404, "ACCOUNT_NOT_FOUND");
        if (target.Id == principal.Id || !ids.Contains(target.Id)) return new(403, "ACCESS_DENIED");
        target.Suspended = suspended;
        await db.SaveChangesAsync(ct);
        return new(200, null);
    }

    public async Task<VisibleDomainsResult> ListAccessibleDomains(AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var principal = await AuthenticateAsync(auth, ct);
        if (principal is null) return new(401, null, "UNAUTHORIZED");
        var ids = await VisibleAccountsAsync(principal, ct);
        var query = from domain in db.Domains.AsNoTracking()
                    join owner in db.Accounts.AsNoTracking() on domain.OwnerId equals owner.Id
                    where ids.Contains(owner.Id)
                    select new VisibleDomain(domain.Name, owner.Username, owner.Kind);
        return new(200, await query.OrderBy(x => x.Domain).ToListAsync(ct), null);
    }
    async Task<VisibleDomainsResult> IAccessibleDomains.ListAsync(AuthenticationHeaderValue auth, CancellationToken ct)
        => await ListAccessibleDomains(auth, ct);

    public async Task<DomainListResult> ListAsync(string? owner, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var principal = await AuthenticateAsync(auth, ct);
        if (principal is null) return new(401, null, "UNAUTHORIZED");
        var ids = await VisibleAccountsAsync(principal, ct);
        var query = from domain in db.Domains.AsNoTracking()
                    join account in db.Accounts.AsNoTracking() on domain.OwnerId equals account.Id
                    where ids.Contains(account.Id) && (owner == null ? account.Id == principal.Id : account.Username == owner)
                    select domain.Name;
        return new(200, await query.OrderBy(x => x).ToListAsync(ct), null);
    }

    public async Task<DomainMutationResult> MutateAsync(DomainMutationRequest input, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var principal = await AuthenticateAsync(auth, ct);
        if (principal is null) return new(401, "UNAUTHORIZED");
        if (!ValidDomain(input.Domain) || input.Confirmation != input.Domain) return new(400, "CONFIRMATION_REQUIRED");
        var name = input.Domain.ToLowerInvariant();
        var existing = await db.Domains.FirstOrDefaultAsync(x => x.Name == name, ct);
        if (input.Action == "create")
        {
            if (existing is not null) return new(409, "DOMAIN_EXISTS");
            db.Domains.Add(new HostedDomain { OwnerId = principal.Id, Name = name,
                SettingsJson = JsonSerializer.Serialize(new { php = input.Php, ssl = input.Ssl, cgi = input.Cgi }) });
        }
        else
        {
            if (existing is null) return new(404, "DOMAIN_NOT_FOUND");
            var ids = await VisibleAccountsAsync(principal, ct);
            if (!ids.Contains(existing.OwnerId)) return new(403, "ACCESS_DENIED");
            if (input.Action == "delete")
            {
                // A domain with a DNS zone or TLS resources must be removed explicitly there first.
                if (await db.DnsZones.AnyAsync(z => z.DomainId == existing.Id, ct)
                    || await db.Certificates.AnyAsync(c => c.DomainId == existing.Id, ct))
                    return new(409, "DOMAIN_HAS_RESOURCES");
                db.Domains.Remove(existing);
            }
            else if (input.Action == "modify")
                existing.SettingsJson = JsonSerializer.Serialize(new { php = input.Php, ssl = input.Ssl, cgi = input.Cgi });
            else return new(400, "INVALID_OPERATION");
        }
        await db.SaveChangesAsync(ct);
        return new(200, null);
    }

    public async Task<DomainDetailResult> GetDetailsAsync(string domain, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var principal = await AuthenticateAsync(auth, ct);
        if (principal is null) return new(401, null, "UNAUTHORIZED");
        var ids = await VisibleAccountsAsync(principal, ct);
        var row = await db.Domains.AsNoTracking().FirstOrDefaultAsync(x => x.Name == domain.ToLowerInvariant(), ct);
        if (row is null) return new(404, null, "DOMAIN_NOT_FOUND");
        if (!ids.Contains(row.OwnerId)) return new(403, null, "ACCESS_DENIED");
        var properties = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(row.SettingsJson) ?? new();
        var settings = properties.ToDictionary(x => x.Key, x => x.Value.ToString());
        settings["domain"] = row.Name;
        return new(200, settings, null);
    }

    public async Task<DomainMutationResult> UpdateSettingAsync(string domain, string setting, string value,
        AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var principal = await AuthenticateAsync(auth, ct);
        if (principal is null) return new(401, "UNAUTHORIZED");
        var ids = await VisibleAccountsAsync(principal, ct);
        var row = await db.Domains.FirstOrDefaultAsync(x => x.Name == domain.ToLowerInvariant(), ct);
        if (row is null) return new(404, "DOMAIN_NOT_FOUND");
        if (!ids.Contains(row.OwnerId)) return new(403, "ACCESS_DENIED");
        if (setting is not ("php" or "ssl" or "cgi") || value is not ("ON" or "OFF"))
            return new(400, "INVALID_SETTING");
        var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(row.SettingsJson) ?? new();
        var plain = values.ToDictionary(x => x.Key, x => x.Value.ToString());
        plain[setting] = value;
        row.SettingsJson = JsonSerializer.Serialize(plain);
        await db.SaveChangesAsync(ct);
        return new(200, null);
    }

    public async Task<DatabaseInventory> ListDatabases(AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var principal = await AuthenticateAsync(auth, ct);
        if (principal is null) return new(401, null, "UNAUTHORIZED");
        return new(200, await db.Databases.AsNoTracking().Where(d => d.OwnerId == principal.Id)
            .OrderBy(d => d.Name).Select(d => d.Name).ToListAsync(ct), null);
    }
    Task<DatabaseInventory> IDatabases.ListAsync(AuthenticationHeaderValue auth, CancellationToken ct)
        => ListDatabases(auth, ct);
    public async Task<DatabaseMembers> UsersAsync(string database, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var principal = await AuthenticateAsync(auth, ct);
        if (principal is null) return new(401, null, "UNAUTHORIZED");
        var row = await db.Databases.AsNoTracking().FirstOrDefaultAsync(x => x.Name == database, ct);
        if (row is null || row.OwnerId != principal.Id) return new(404, null, "DATABASE_NOT_FOUND");
        var names = from grant in db.DatabaseGrants.AsNoTracking()
                    join user in db.DatabaseUsers.AsNoTracking() on grant.DatabaseUserId equals user.Id
                    where grant.DatabaseId == row.Id
                    select user.Name;
        return new(200, await names.ToListAsync(ct), null);
    }
    public async Task<DatabaseMutation> CreateAsync(DatabaseCreateRequest input, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var principal = await AuthenticateAsync(auth, ct);
        if (principal is null) return new(401, "UNAUTHORIZED");
        if (!ValidDb(input.Database) || !ValidDb(input.DbUser) || input.Password.Length < 12)
            return new(400, "INVALID_DATABASE_REQUEST");
        if (await db.Databases.AnyAsync(x => x.Name == input.Database, ct))
            return new(409, "DATABASE_EXISTS");
        var user = await db.DatabaseUsers.FirstOrDefaultAsync(x => x.OwnerId == principal.Id && x.Name == input.DbUser, ct);
        if (user is null)
        {
            user = new DatabaseUser { OwnerId = principal.Id, Name = input.DbUser };
            db.DatabaseUsers.Add(user);
        }
        // DB passwords must be provisioned into a Kubernetes Secret by a separate
        // credential-provider component. Do not persist them in the panel database.
        // Until that provider exists, refuse the mutation instead of claiming success.
        return new(501, "KUBERNETES_CREDENTIAL_PROVISIONING_NOT_IMPLEMENTED");
    }
    public async Task<DatabaseMutation> DeleteAsync(string database, string confirmation, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var principal = await AuthenticateAsync(auth, ct);
        if (principal is null) return new(401, "UNAUTHORIZED");
        if (database != confirmation) return new(400, "CONFIRMATION_REQUIRED");
        var row = await db.Databases.FirstOrDefaultAsync(x => x.Name == database, ct);
        if (row is null) return new(404, "DATABASE_NOT_FOUND");
        if (row.OwnerId != principal.Id) return new(403, "ACCESS_DENIED");
        // Deprovisioning a physical database must precede deleting its desired-state record.
        return new(501, "KUBERNETES_DATABASE_DEPROVISIONING_NOT_IMPLEMENTED");
    }

    public async Task<AccessibleDatabaseInventory> ListAccessibleDatabases(AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var principal = await AuthenticateAsync(auth, ct);
        if (principal is null) return new(401, null, null, "UNAUTHORIZED");
        var ids = await VisibleAccountsAsync(principal, ct);
        var entries = await (from d in db.Databases.AsNoTracking()
            join o in db.Accounts.AsNoTracking() on d.OwnerId equals o.Id
            where ids.Contains(o.Id)
            select new { o.Username, d.Name, d.Engine }).ToListAsync(ct);
        var items = entries.Select(x => new OwnedDatabase(x.Username,
            JsonSerializer.SerializeToElement(new { name = x.Name, type = x.Engine }))).ToArray();
        return new(200, items, Array.Empty<DatabaseInventoryWarning>(), null);
    }
    Task<AccessibleDatabaseInventory> IAccessibleDatabases.ListAsync(AuthenticationHeaderValue auth, CancellationToken ct)
        => ListAccessibleDatabases(auth, ct);

    public async Task<AdminDnsZoneListResult> ListAdminZonesAsync(AuthenticationHeaderValue auth, int page, CancellationToken ct)
    {
        var principal = await AuthenticateAsync(auth, ct);
        if (principal is null) return new(401, null, "UNAUTHORIZED");
        if (page < 1 || page > 10000) return new(400, null, "INVALID_PAGE");
        var ids = await VisibleAccountsAsync(principal, ct);
        var domainIds = await db.Domains.AsNoTracking().Where(d => ids.Contains(d.OwnerId))
            .Select(d => d.Id).ToListAsync(ct);
        var zones = await db.DnsZones.AsNoTracking().Where(z => z.DomainId == null || domainIds.Contains(z.DomainId.Value))
            .OrderBy(z => z.Name).Skip((page - 1) * 100).Take(100)
            .Select(z => new AdminDnsZone(z.Name, null, null)).ToListAsync(ct);
        return new(200, zones, null);
    }

    public async Task<DnsZoneResult> ReadAsync(string domain, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var principal = await AuthenticateAsync(auth, ct);
        if (principal is null) return new(401, null, "UNAUTHORIZED");
        var ids = await VisibleAccountsAsync(principal, ct);
        var zone = await db.DnsZones.AsNoTracking().FirstOrDefaultAsync(z => z.Name == domain.ToLowerInvariant(), ct);
        if (zone is null) return new(404, null, "ZONE_NOT_FOUND");
        var host = zone.DomainId is null ? null :
            await db.Domains.AsNoTracking().FirstOrDefaultAsync(d => d.Id == zone.DomainId, ct);
        if (host is null || !ids.Contains(host.OwnerId)) return new(403, null, "ACCESS_DENIED");
        var records = await db.DnsRecords.AsNoTracking().Where(x => x.ZoneId == zone.Id)
            .OrderBy(x => x.Name).ThenBy(x => x.Type).ToListAsync(ct);
        return new(200, records.Select(r => new DnsRecord(r.Type, r.Name, r.Value, r.Ttl.ToString())).ToArray(), null);
    }

    public async Task<DnsChangeResult> ChangeAsync(string domain, DnsChange change, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var principal = await AuthenticateAsync(auth, ct);
        if (principal is null) return new(401, "UNAUTHORIZED");
        if (!ValidDomain(domain) || !LegacyDnsAdapter.Valid(change.Record)
            || change.Action is not ("add" or "edit" or "delete")
            || (change.Action != "add" && (change.Previous is null || !LegacyDnsAdapter.Valid(change.Previous))))
            return new(400, "INVALID_DNS_CHANGE");
        var ids = await VisibleAccountsAsync(principal, ct);
        var zone = await db.DnsZones.FirstOrDefaultAsync(x => x.Name == domain.ToLowerInvariant(), ct);
        if (zone is null) return new(404, "ZONE_NOT_FOUND");
        var host = await db.Domains.FirstOrDefaultAsync(d => d.Id == zone.DomainId, ct);
        if (host is null || !ids.Contains(host.OwnerId)) return new(403, "ACCESS_DENIED");
        var prev = change.Previous;
        var previous = prev is null ? null : await db.DnsRecords.FirstOrDefaultAsync(x =>
            x.ZoneId == zone.Id && x.Name == prev.Name && x.Type == prev.Type && x.Value == prev.Value, ct);
        if (change.Action != "add" && previous is null) return new(404, "DNS_RECORD_NOT_FOUND");
        if (previous is not null) db.DnsRecords.Remove(previous);
        if (change.Action != "delete") db.DnsRecords.Add(new DnsRecordEntity {
            ZoneId = zone.Id, Name = change.Record.Name, Type = change.Record.Type,
            Value = change.Record.Value, Ttl = int.TryParse(change.Record.Ttl, out var ttl) ? ttl : zone.DefaultTtl
        });
        await db.SaveChangesAsync(ct);
        return new(200, null);
    }

    public async Task<DnsChangeResult> CreateZoneAsync(DnsZoneCreate input, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var principal = await AuthenticateAsync(auth, ct);
        if (principal is null) return new(401, "UNAUTHORIZED");
        if (!ValidDomain(input.Domain)) return new(400, "INVALID_DOMAIN");
        var domain = await db.Domains.FirstOrDefaultAsync(d => d.Name == input.Domain.ToLowerInvariant(), ct);
        if (domain is null) return new(404, "DOMAIN_NOT_FOUND");
        var ids = await VisibleAccountsAsync(principal, ct);
        if (!ids.Contains(domain.OwnerId)) return new(403, "ACCESS_DENIED");
        if (await db.DnsZones.AnyAsync(z => z.Name == domain.Name, ct)) return new(409, "ZONE_EXISTS");
        db.DnsZones.Add(new DnsZone { Name = domain.Name, DomainId = domain.Id });
        await db.SaveChangesAsync(ct);
        return new(200, null);
    }

    public async Task<DnsChangeResult> DeleteZoneAsync(string domain, string confirmation, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var principal = await AuthenticateAsync(auth, ct);
        if (principal is null) return new(401, "UNAUTHORIZED");
        if (domain != confirmation) return new(400, "CONFIRMATION_REQUIRED");
        var zone = await db.DnsZones.FirstOrDefaultAsync(z => z.Name == domain.ToLowerInvariant(), ct);
        if (zone is null) return new(404, "ZONE_NOT_FOUND");
        var host = await db.Domains.FirstOrDefaultAsync(d => d.Id == zone.DomainId, ct);
        var ids = await VisibleAccountsAsync(principal, ct);
        if (host is null || !ids.Contains(host.OwnerId)) return new(403, "ACCESS_DENIED");
        db.DnsZones.Remove(zone);
        await db.SaveChangesAsync(ct);
        return new(200, null);
    }

    public async Task<AccountOverviewResult> GetAsync(string username, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var principal = await AuthenticateAsync(auth, ct);
        if (principal is null) return new(401, null, null, null, "UNAUTHORIZED");
        var ids = await VisibleAccountsAsync(principal, ct);
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

    public Task<FileAccessResult> ListFtpAsync(string domain, AuthenticationHeaderValue auth, CancellationToken ct)
        => Task.FromResult(new FileAccessResult(501, "KUBERNETES_FTP_NOT_IMPLEMENTED"));
    public Task<FileAccessResult> GetFtpAsync(string domain, string user, AuthenticationHeaderValue auth, CancellationToken ct)
        => Task.FromResult(new FileAccessResult(501, "KUBERNETES_FTP_NOT_IMPLEMENTED"));
    public Task<FileAccessResult> CreateFtpAsync(FtpAccountRequest input, AuthenticationHeaderValue auth, CancellationToken ct)
        => Task.FromResult(new FileAccessResult(501, "KUBERNETES_FTP_NOT_IMPLEMENTED"));
    public Task<FileAccessResult> UpdateFtpAsync(string user, FtpAccountRequest input, AuthenticationHeaderValue auth, CancellationToken ct)
        => Task.FromResult(new FileAccessResult(501, "KUBERNETES_FTP_NOT_IMPLEMENTED"));
    public Task<FileAccessResult> DeleteFtpAsync(string domain, string user, AuthenticationHeaderValue auth, CancellationToken ct)
        => Task.FromResult(new FileAccessResult(501, "KUBERNETES_FTP_NOT_IMPLEMENTED"));

    public IResult PreviewDomain(DomainCreatePreview input)
    {
        if (!ValidDomain(input.Domain))
            return Results.Json(new { type = "INVALID_DOMAIN" }, statusCode: 400);
        return Results.Json(new {
            domain = input.Domain.ToLowerInvariant(), dryRun = true,
            desiredSettings = new { php = input.Php, ssl = input.Ssl, cgi = input.Cgi },
            executed = false
        });
    }
    public Task ForwardAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status501NotImplemented;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsJsonAsync(new { type = "KUBERNETES_OPERATION_NOT_IMPLEMENTED" });
    }
}

public static class PasswordVerifier
{
    private const int Iterations = 210_000;
    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }
    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256"
            || !int.TryParse(parts[1], out var iterations)
            || iterations < 100_000 || iterations > 2_000_000) return false;
        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            if (salt.Length != 16 || expected.Length != 32) return false;
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException) { return false; }
    }
}
