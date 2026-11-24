using System.Net.Http.Headers;
using System.Text.Json;
using HostingPanel.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HostingPanel.Api;

public sealed class KubernetesAccountNameservers(HostingPanelDbContext db, KubernetesIdentityService identity, KubernetesSettingsService settings) : IAccountNameservers
{
    internal static string Key(Guid id) => "AccountNameservers:" + id.ToString("N");

    internal static async Task<AccountNameserversUpdate?> ReadOverrideAsync(HostingPanelDbContext db, Guid id, CancellationToken ct)
    {
        var row = await db.Configuration.AsNoTracking().SingleOrDefaultAsync(x => x.Key == Key(id), ct);
        return row is null ? null : JsonSerializer.Deserialize<AccountNameserversUpdate>(row.ValueJson);
    }

    internal static async Task<string[]> ResolveDefaultsAsync(HostingPanelDbContext db, HostingAccount owner, KubernetesBackendSettings settings, CancellationToken ct)
    {
        var parent = owner.CreatorId is { } creator
            ? db.Accounts.Local.FirstOrDefault(x => x.Id == creator) ?? await db.Accounts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == creator, ct) : null;
        var assigned = settings.IpAddresses.Where(x => (x.AssignedUsers ?? []).Contains(owner.Username)).Select(x => x.NameServer);
        var inherited = settings.IpAddresses.Where(x => (x.Resellers ?? []).Contains(owner.Username) ||
            (parent is not null && (x.Resellers ?? []).Contains(parent.Username)) ||
            (parent is not null && (x.AssignedUsers ?? []).Contains(parent.Username))).Select(x => x.NameServer);
        return assigned.Concat(inherited).Concat(new[] { settings.DnsNs1, settings.DnsNs2 })
            .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.TrimEnd('.'))
            .Where(KubernetesInputValidation.ValidDomain).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
    }

    internal static async Task<string[]> ResolveAsync(HostingPanelDbContext db, HostingAccount owner, KubernetesBackendSettings settings, CancellationToken ct)
    {
        var custom = await ReadOverrideAsync(db, owner.Id, ct);
        return custom is { Ns1: not null, Ns2: not null } ? [custom.Ns1, custom.Ns2] : await ResolveDefaultsAsync(db, owner, settings, ct);
    }

    public async Task<IResult> ReadAsync(AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var owner = await identity.AuthenticateAsync(auth, ct);
        if (owner is null) return Results.Unauthorized();
        var custom = await ReadOverrideAsync(db, owner.Id, ct);
        var defaults = await ResolveDefaultsAsync(db, owner, await settings.GetEffectiveSettingsAsync(ct), ct);
        return Results.Ok(new AccountNameserversView(custom?.Ns1, custom?.Ns2, custom?.Ns1 ?? defaults.ElementAtOrDefault(0), custom?.Ns2 ?? defaults.ElementAtOrDefault(1), true, custom is null));
    }

    public async Task<IResult> WriteAsync(AuthenticationHeaderValue auth, AccountNameserversUpdate update, CancellationToken ct)
    {
        var owner = await identity.AuthenticateAsync(auth, ct);
        if (owner is null) return Results.Unauthorized();
        if (!AccountNameserverValidation.TryNormalize(update, out var normalized))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["nameservers"] = ["Specify two distinct valid nameservers, or leave both empty."] });
        var key = Key(owner.Id);
        var row = await db.Configuration.SingleOrDefaultAsync(x => x.Key == key, ct);
        if (normalized.Ns1 is null)
        {
            if (row is not null) db.Configuration.Remove(row);
        }
        else
        {
            if (row is null) { row = new PanelConfiguration { Key = key }; db.Configuration.Add(row); }
            row.ValueJson = JsonSerializer.Serialize(normalized);
        }
        await db.SaveChangesAsync(ct);
        return await ReadAsync(auth, ct);
    }
}
