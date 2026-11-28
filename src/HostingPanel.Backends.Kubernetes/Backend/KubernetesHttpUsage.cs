using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using HostingPanel.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HostingPanel.Api;

public sealed class KubernetesHttpUsage(HostingPanelDbContext db,KubernetesIdentityService identity) : IHttpUsage
{
    public async Task<HttpUsageResult> ReadAsync(string username,string? month,AuthenticationHeaderValue auth,CancellationToken ct)
    {
        var principal = await identity.AuthenticateAsync(auth,ct);
        if (principal is null) return new(401,"UNAUTHORIZED");
        var visible = await identity.VisibleAccountsAsync(principal,ct);
        var owner = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(x=>x.Username==username && visible.Contains(x.Id),ct);
        if (owner is null) return new(404,"ACCOUNT_NOT_FOUND");
        month ??= DateTimeOffset.UtcNow.ToString("yyyy-MM",CultureInfo.InvariantCulture);
        if (!DateTime.TryParseExact(month,"yyyy-MM",CultureInfo.InvariantCulture,DateTimeStyles.None,out _)) return new(400,"INVALID_MONTH");
        return await ReadOwnerAsync(owner,month,ct);
    }

    internal async Task<HttpUsageResult> ReadOwnerAsync(HostingAccount owner,string month,CancellationToken ct)
    {
        var rows=await db.Set<ApacheMonthlyUsage>().AsNoTracking().Where(x=>x.OwnerId==owner.Id && x.Month==month).ToListAsync(ct);
        var package=await db.Packages.AsNoTracking().Where(x=>x.Id==owner.PackageId).Select(x=>x.LimitsJson).SingleOrDefaultAsync(ct);
        var (known,limit)=ResolveLimit(package,owner.LimitsJson);
        return new(200,Month:month,ReceivedBytes:rows.Sum(x=>x.ReceivedBytes),SentBytes:rows.Sum(x=>x.SentBytes),Requests:rows.Sum(x=>x.Requests),BandwidthLimitBytes:limit,LimitKnown:known);
    }

    public static (bool Known,long? Bytes) ResolveLimit(string? package,string? account)
    {
        static Dictionary<string,string> Fields(string? json) => JsonSerializer.Deserialize<Dictionary<string,string>>(json??"{}")??new();
        var raw=Fields(account).GetValueOrDefault("bandwidth")??Fields(package).GetValueOrDefault("bandwidth");
        if(raw=="unlimited") return (true,null);
        if(!long.TryParse(raw,NumberStyles.None,CultureInfo.InvariantCulture,out var mib) || mib<0 || mib>long.MaxValue/1048576) return (false,null);
        return (true,mib*1048576);
    }
}
