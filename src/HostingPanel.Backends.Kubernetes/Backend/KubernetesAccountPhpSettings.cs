using System.Net.Http.Headers;
using System.Text.Json;
using HostingPanel.Api.Persistence;
using HostingPanel.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace HostingPanel.Api;

public sealed class KubernetesAccountPhpSettings(HostingPanelDbContext db, KubernetesIdentityService identity,
    IKubernetesSettingsReader settings, HostingOperationNotifications notifications) : IAccountPhpSettings
{
    private async Task<(int Status, HostingAccount? Account)> TargetAsync(string username, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var principal = await identity.AuthenticateAsync(auth, ct);
        if (principal is null) return (401, null);
        if (principal.Kind is not ("admin" or "reseller")) return (403, null);
        var row = await db.Accounts.SingleOrDefaultAsync(x => x.Username == username, ct);
        if (row is null) return (404, null);
        var visible = await identity.VisibleAccountsAsync(principal, ct);
        if (row.Id == principal.Id || !visible.Contains(row.Id) ||
            (principal.Kind == "reseller" && row.Kind != "user")) return (403, null);
        return (200, row);
    }

    private async Task<string?> PackageAsync(HostingAccount row, CancellationToken ct) =>
        await db.Packages.Where(x => x.Id == row.PackageId).Select(x => x.LimitsJson).SingleOrDefaultAsync(ct);

    public async Task<PhpRuntimeView> DefaultsAsync(AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var principal = await identity.AuthenticateAsync(auth, ct);
        if (principal is null) return new(401, null, []);
        if (principal.Kind is not ("admin" or "reseller")) return new(403, null, []);
        var config = await settings.GetEffectiveSettingsAsync(ct);
        return new(200, PhpRuntimePolicy.Default(config), PhpRuntimePolicy.Images(config));
    }

    public async Task<PhpRuntimeView> ReadAsync(string username, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var (status, row) = await TargetAsync(username, auth, ct);
        if (row is null) return new(status, null, []);
        var config = await settings.GetEffectiveSettingsAsync(ct);
        return new(200, PhpRuntimePolicy.Resolve(config, await PackageAsync(row, ct), row.LimitsJson), PhpRuntimePolicy.Images(config));
    }

    public async Task<AccountOperationResult> WriteAsync(string username, PhpRuntimeSettings? update, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (status, row) = await TargetAsync(username, auth, ct);
        if (row is null) return new(status, "ACCESS_DENIED");
        var config = await settings.GetEffectiveSettingsAsync(ct);
        if (update is not null && !update.Valid(PhpRuntimePolicy.Images(config))) return new(400, "INVALID_PHP_RUNTIME");
        var fields = PhpRuntimePolicy.Fields(row.LimitsJson);
        if (update is null) fields.Remove(PhpRuntimePolicy.Key);
        else fields[PhpRuntimePolicy.Key] = JsonSerializer.Serialize(update);
        row.LimitsJson = JsonSerializer.Serialize(fields);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        notifications.Publish();
        return new(202, null);
    }
}
