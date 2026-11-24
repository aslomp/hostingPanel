using System.Net.Http.Headers;
using System.Text;
using HostingPanel.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HostingPanel.Api;

/// <summary>Authenticates Kubernetes-backed accounts and resolves account visibility.</summary>
public sealed class KubernetesIdentityService(HostingPanelDbContext db) : IAuthenticationProvider
{
    public async Task<int> AuthenticateSessionAsync(AuthenticationHeaderValue auth, CancellationToken ct) =>
        await AuthenticateAsync(auth, ct) is null ? 401 : 200;

    public async Task<HostingAccount?> AuthenticateAsync(AuthenticationHeaderValue auth, CancellationToken ct)
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
    public async Task<HashSet<Guid>> VisibleAccountsAsync(HostingAccount principal, CancellationToken ct)
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

}
