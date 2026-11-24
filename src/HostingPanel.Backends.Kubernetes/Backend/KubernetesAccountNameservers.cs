using System.Net.Http.Headers;
using HostingPanel.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HostingPanel.Api;

public sealed class KubernetesAccountNameservers(HostingPanelDbContext db, KubernetesIdentityService identity, KubernetesSettingsService settings) : IAccountNameservers
{
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

    internal static Task<string[]> ResolveAsync(HostingPanelDbContext db, HostingAccount owner, KubernetesBackendSettings settings, CancellationToken ct)
        => ResolveDefaultsAsync(db, owner, settings, ct);

    public async Task<IResult> ReadAsync(AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var owner = await identity.AuthenticateAsync(auth, ct);
        if (owner is null) return Results.Unauthorized();
        var effective = await ResolveAsync(db, owner, await settings.GetEffectiveSettingsAsync(ct), ct);
        return Results.Ok(new AccountNameserversView(null, null, effective.ElementAtOrDefault(0), effective.ElementAtOrDefault(1), false, true));
    }

    public async Task<IResult> WriteAsync(AuthenticationHeaderValue auth, AccountNameserversUpdate update, CancellationToken ct)
        => await identity.AuthenticateAsync(auth, ct) is null ? Results.Unauthorized() : Results.StatusCode(403);
}
