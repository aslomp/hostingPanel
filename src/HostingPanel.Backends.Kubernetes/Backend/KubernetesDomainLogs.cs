using System.Net.Http.Headers;
using HostingPanel.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HostingPanel.Api;

internal sealed class KubernetesDomainLogs(HostingPanelDbContext db, KubernetesIdentityService identity) : IDomainLogs
{
    public async Task<DomainLogResult> ReadAsync(string domain, string kind, long? before, int count, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var principal = await identity.AuthenticateAsync(auth, ct);
        if (principal is null) return new(401,"UNAUTHORIZED");
        if (kind is not ("access" or "error") || count is < 1 or > 1000 || before is <= 0) return new(400,"INVALID_LOG_QUERY");
        var ids = await identity.VisibleAccountsAsync(principal, ct);
        var site = await db.Domains.AsNoTracking().SingleOrDefaultAsync(x => x.Name == domain.ToLowerInvariant() && ids.Contains(x.OwnerId),ct);
        if (site is null) return new(404,"DOMAIN_NOT_FOUND");
        var rows = await db.Set<ApacheLogRow>().AsNoTracking().Where(x => x.DomainId == site.Id && x.OwnerId == site.OwnerId && x.Kind == kind && (!before.HasValue || x.Id < before.Value))
            .OrderByDescending(x => x.Id).Take(count).Select(x => new DomainLogEntry(x.Id,x.Timestamp,x.Message)).ToListAsync(ct);
        return new(200,Entries:rows);
    }
}
