using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HostingPanel.Api.Persistence;
using HostingPanel.Providers.CertManager;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using k8s;

namespace HostingPanel.Api;

// Pod watch starts one following stream per container incarnation. SQLite receipts and
// aggregates commit together; reconnect replays the currently available container log.
internal sealed class ApacheLogCollector(IServiceScopeFactory scopes, IConfiguration configuration, KubernetesResourceClient resources, ILogger<ApacheLogCollector> logger) : BackgroundService
{
    private sealed record Source(string Name,string Identity);
    private sealed record Worker(Source Source,CancellationTokenSource Stop,Task Task);
    private readonly Dictionary<string,Worker> workers = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim writer = new(1);
    private string Namespace => configuration["KubernetesHosting:Namespace"] ?? "hosting";

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!configuration.GetValue<bool>("KubernetesHosting:Enabled")) return;
        try
        {
            await resources.WatchApacheLogPodsAsync(Namespace, json => {
                using var doc = JsonDocument.Parse(json);
                var desired = doc.RootElement.GetProperty("items").EnumerateArray().Select(ReadSource).OfType<Source>().ToDictionary(x => x.Name);
                foreach (var name in workers.Keys.Except(desired.Keys).ToArray()) Remove(name);
                foreach (var source in desired.Values) Start(source,ct);
            }, (kind,json) => {
                using var doc = JsonDocument.Parse(json);
                var name = doc.RootElement.GetProperty("metadata").GetProperty("name").GetString()!;
                if (kind == WatchEventType.Deleted) Remove(name);
                else if (ReadSource(doc.RootElement) is { } source) Start(source,ct);
            },ct);
        }
        finally
        {
            var active = workers.Values.ToArray();
            foreach (var worker in active) worker.Stop.Cancel();
            await Task.WhenAll(active.Select(x => x.Task));
            foreach (var worker in active) worker.Stop.Dispose();
        }
    }

    private static Source? ReadSource(JsonElement pod)
    {
        var meta = pod.GetProperty("metadata");
        var name = meta.GetProperty("name").GetString();
        var uid = meta.GetProperty("uid").GetString();
        if (name is null || uid is null || !name.StartsWith("apache-baseline-",StringComparison.Ordinal) ||
            !pod.TryGetProperty("status",out var status) || !status.TryGetProperty("containerStatuses",out var containers)) return null;
        foreach (var container in containers.EnumerateArray())
            if (container.GetProperty("name").GetString() == "apache" && container.TryGetProperty("containerID",out var id) && id.GetString() is { Length: > 0 } value &&
                container.TryGetProperty("state",out var state) && state.TryGetProperty("running",out _))
                return new(name,uid + "/" + value);
        return null;
    }

    private void Remove(string name)
    {
        if (!workers.Remove(name,out var old)) return;
        old.Stop.Cancel();
        _ = old.Task.ContinueWith(_ => old.Stop.Dispose(),TaskScheduler.Default);
    }
    private void Start(Source source,CancellationToken ct)
    {
        if (workers.TryGetValue(source.Name,out var old) && old.Source == source) return;
        Remove(source.Name);
        var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        workers[source.Name] = new(source,stop,FollowAsync(source,stop.Token));
    }

    private async Task FollowAsync(Source source,CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var stream = await resources.FollowApacheLogAsync(Namespace,source.Name,ct);
                using var reader = new StreamReader(stream);
                var occurrences = new Dictionary<string,int>(StringComparer.Ordinal);
                string? currentTimestamp = null;
                while (await reader.ReadLineAsync(ct) is { } line)
                {
                    var split = line.IndexOf(' ');
                    if (split < 1 || line.Length > 16384) continue;
                    var timestamp = line[..split];
                    if (!DateTimeOffset.TryParse(timestamp,CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var time)) continue;
                    var payload = line[(split+1)..];
                    if (!TryDecode(payload,time,out var row,out var access)) continue;
                    if (currentTimestamp != timestamp) { occurrences.Clear(); currentTimestamp = timestamp; }
                    var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
                    var ordinal = occurrences.GetValueOrDefault(digest) + 1;
                    occurrences[digest] = ordinal;
                    await PersistAsync(source.Identity,timestamp,digest,ordinal,row!,access,ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning("Apache accounting stream interrupted for {Pod}: {Error}",source.Name,ex.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromSeconds(5),ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        }
    }

    internal static bool TryDecode(string payload,DateTimeOffset timestamp,out ApacheLogRow? row,out ApacheAccessRecord? access)
    {
        row = null;
        if (ApacheAccessAccounting.TryParse(payload,out access))
        {
            row = new() { OwnerId=access!.AccountId,DomainId=access.DomainId,Kind="access",Timestamp=timestamp.ToUniversalTime().ToString("O"),Message=payload };
            return true;
        }
        var parts = payload.Split(' ',4,StringSplitOptions.None);
        if (parts.Length != 4 || parts[0] != "HP_ERROR_V1" || !Guid.TryParseExact(parts[1],"N",out var owner) || owner == Guid.Empty ||
            !Guid.TryParseExact(parts[2],"N",out var domain) || domain == Guid.Empty || parts[3].Any(char.IsControl)) return false;
        row = new() { OwnerId=owner,DomainId=domain,Kind="error",Timestamp=timestamp.ToUniversalTime().ToString("O"),Message=parts[3] };
        return true;
    }

    private async Task PersistAsync(string source,string timestamp,string digest,int ordinal,ApacheLogRow row,ApacheAccessRecord? access,CancellationToken ct)
    {
        await writer.WaitAsync(ct);
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HostingPanelDbContext>();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (await db.Set<ApacheLogReceipt>().FindAsync([source,timestamp,digest,ordinal],ct) is not null) return;
            // Do not attribute unknown/reassigned domains to another account.
            if (!await db.Domains.AsNoTracking().AnyAsync(x => x.Id == row.DomainId && x.OwnerId == row.OwnerId,ct)) return;
            db.Add(new ApacheLogReceipt { Source=source,Timestamp=timestamp,Digest=digest,Occurrence=ordinal });
            db.Add(row);
            if (access is not null)
            {
                var month = access.CompletedAt.ToString("yyyy-MM",CultureInfo.InvariantCulture);
                var usage = await db.Set<ApacheMonthlyUsage>().FindAsync([row.OwnerId,row.DomainId,month],ct);
                if (usage is null) { usage = new() { OwnerId=row.OwnerId,DomainId=row.DomainId,Month=month }; db.Add(usage); }
                checked { usage.ReceivedBytes += access.ReceivedBytes; usage.SentBytes += access.SentBytes; usage.Requests++; }
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        finally { writer.Release(); }
    }
}
