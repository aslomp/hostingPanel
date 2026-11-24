using System.Net.Http.Headers;
using System.Text.Json;
using HostingPanel.Api.Persistence;
using HostingPanel.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace HostingPanel.Api;

public sealed class KubernetesPackageManagement(HostingPanelDbContext db, KubernetesIdentityService identity) : IPackageManagement
{
    // Existing packages without a kind marker are user packages.
    // Keep the marker in the existing package settings, not in a second inventory.
    private const string KindKey = "_hostingPanelKind";
    internal static Dictionary<string,string> Settings(HostingPackage p) =>
        JsonSerializer.Deserialize<Dictionary<string,string>>(p.LimitsJson) ?? new();
    internal static bool Matches(HostingPackage p, string kind) =>
        (Settings(p).GetValueOrDefault(KindKey) ?? "user") == kind;

    private async Task<(int Status, HostingAccount? Principal)> Authorize(string kind, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var principal = await identity.AuthenticateAsync(auth, ct);
        if (principal is null) return (401, null);
        if (!PackageFields.ValidKind(kind)) return (400, null);
        if (principal.Kind != "admin" && !(principal.Kind == "reseller" && kind == "user")) return (403, null);
        return (200, principal);
    }

    private Task<List<HostingPackage>> Available(HostingAccount principal, CancellationToken ct) =>
        db.Packages.Where(p => p.OwnerId == principal.Id || p.OwnerId == null).ToListAsync(ct);

    private static string Error(int status) => status switch { 401 => "UNAUTHORIZED", 403 => "ACCESS_DENIED", _ => "INVALID_ACCOUNT_KIND" };

    public async Task<AccountPackagesResult> ListAsync(string kind, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var (status, principal) = await Authorize(kind, auth, ct);
        if (principal is null) return new(status, null, Error(status));
        var rows = await Available(principal, ct);
        return new(200, rows.Where(p => Matches(p, kind)).Select(p => p.Name).Distinct(StringComparer.Ordinal).OrderBy(n => n).ToArray(), null);
    }

    private static HostingPackage? Find(IEnumerable<HostingPackage> rows, string kind, string name, Guid owner) =>
        rows.Where(p => p.Name == name && Matches(p, kind)).OrderByDescending(p => p.OwnerId == owner).FirstOrDefault();

    public async Task<PackageDetailsResult> GetAsync(string kind, string name, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var (status, principal) = await Authorize(kind, auth, ct);
        if (principal is null) return new(status, null, Error(status));
        var row = Find(await Available(principal, ct), kind, name, principal.Id);
        if (row is null) return new(404, null, "PACKAGE_NOT_FOUND");
        var fields = Settings(row);
        fields.Remove(KindKey);
        return new(200, new(row.Name, fields), null);
    }

    public async Task<AccountOperationResult> SaveAsync(string kind, HostingPackageDefinition package, bool create, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var (status, principal) = await Authorize(kind, auth, ct);
        if (principal is null) return new(status, Error(status));
        if (!PackageFields.ValidName(package.Name) || !PackageFields.ValidSettings(kind, package.Settings))
            return new(400, "INVALID_PACKAGE");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var rows = await Available(principal, ct);
        var row = Find(rows, kind, package.Name, principal.Id);
        if (create)
        {
            // Existing schema has a unique owner/name index across kinds.
            if (rows.Any(p => p.Name == package.Name)) return new(409, "PACKAGE_EXISTS");
            row = new HostingPackage { Name = package.Name, OwnerId = principal.Id };
            db.Packages.Add(row);
        }
        else if (row is null) return new(404, "PACKAGE_NOT_FOUND");
        else if (row.OwnerId != principal.Id && !(row.OwnerId == null && principal.Kind == "admin"))
            return new(403, "ACCESS_DENIED");
        var fields = Settings(row);
        foreach (var field in package.Settings) fields[field.Key] = field.Value;
        fields[KindKey] = kind;
        row.LimitsJson = JsonSerializer.Serialize(fields);
        try { await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); }
        catch (DbUpdateException) { return new(409, "PACKAGE_CONFLICT"); }
        return new(create ? 201 : 200, null);
    }

    public async Task<AccountOperationResult> DeleteAsync(string kind, string name, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var (status, principal) = await Authorize(kind, auth, ct);
        if (principal is null) return new(status, Error(status));
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var row = Find(await Available(principal, ct), kind, name, principal.Id);
        if (row is null) return new(404, "PACKAGE_NOT_FOUND");
        if (row.OwnerId != principal.Id && !(row.OwnerId == null && principal.Kind == "admin")) return new(403, "ACCESS_DENIED");
        if (await db.Accounts.AnyAsync(a => a.PackageId == row.Id, ct)) return new(409, "PACKAGE_IN_USE");
        db.Packages.Remove(row);
        try { await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); }
        catch (DbUpdateException) { return new(409, "PACKAGE_IN_USE"); }
        return new(200, null);
    }
}
