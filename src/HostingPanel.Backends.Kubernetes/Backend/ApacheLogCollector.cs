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
    internal sealed record Pending(string Source,string Timestamp,string Digest,int Ordinal,ApacheLogRow Row,ApacheAccessRecord? Access);
    private DateTimeOffset nextRetention = DateTimeOffset.MinValue;
    private int RetentionDays => Math.Max(1, configuration.GetValue("KubernetesHosting:LogRetentionDays",30));
    private string Namespace => configuration["KubernetesHosting:Namespace"] ?? "hosting";

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!configuration.GetValue<bool>("KubernetesHosting:Enabled") || configuration.GetValue<bool>("KubernetesHosting:DurableLogIngestion")) return;
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
                var batch = new List<Pending>(256);
                var read = reader.ReadLineAsync(ct).AsTask();
                while (true)
                {
                    if (batch.Count != 0 && !read.IsCompleted)
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        var deadline = Task.Delay(TimeSpan.FromMilliseconds(250),timeout.Token);
                        if (await Task.WhenAny(read,deadline) == deadline)
                        {
                            ct.ThrowIfCancellationRequested();
                            await PersistAsync(batch,ct);
                            batch.Clear();
                            continue;
                        }
                        timeout.Cancel();
                    }
                    var line = await read;
                    if (line is null) break;
                    read = reader.ReadLineAsync(ct).AsTask();
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
                    batch.Add(new(source.Identity,timestamp,digest,ordinal,row!,access));
                    if (batch.Count >= 256)
                    {
                        await PersistAsync(batch,ct);
                        batch.Clear();
                    }
                }
                if (batch.Count != 0) await PersistAsync(batch,ct);
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

    internal async Task PersistAsync(IReadOnlyList<Pending> batch,CancellationToken ct, Guid? segmentId = null, string? segmentDigest = null)
    {
        await writer.WaitAsync(ct);
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HostingPanelDbContext>();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (segmentId is { } segment)
            {
                var receipt = await db.Set<ApacheSegmentReceipt>().FindAsync([segment],ct);
                if (receipt is not null)
                {
                    if (receipt.Digest != segmentDigest) throw new InvalidDataException("Conflicting accounting segment.");
                    return;
                }
                db.Add(new ApacheSegmentReceipt { Id=segment, Digest=segmentDigest ?? throw new InvalidDataException("Missing segment digest.") });
            }
            var ids = batch.Select(x=>x.Row.DomainId).Distinct().ToArray();
            var owners = await db.Domains.AsNoTracking().Where(x=>ids.Contains(x.Id)).ToDictionaryAsync(x=>x.Id,x=>x.OwnerId,ct);
            var now = DateTimeOffset.UtcNow;
            var cutoff = now.AddDays(-RetentionDays).ToString("O",CultureInfo.InvariantCulture);
            foreach (var item in batch)
            {
                var row = item.Row;
                if (!owners.TryGetValue(row.DomainId,out var owner) || owner != row.OwnerId) continue;
                if (await db.Set<ApacheLogReceipt>().FindAsync([item.Source,item.Timestamp,item.Digest,item.Ordinal],ct) is not null) continue;
                db.Add(new ApacheLogReceipt { Source=item.Source,Timestamp=item.Timestamp,Digest=item.Digest,Occurrence=item.Ordinal });
                if (string.CompareOrdinal(row.Timestamp,cutoff)>=0) db.Add(row);
                if (item.Access is not { } access) continue;
                var month = access.CompletedAt.ToString("yyyy-MM",CultureInfo.InvariantCulture);
                var usage = await db.Set<ApacheMonthlyUsage>().FindAsync([row.OwnerId,row.DomainId,month],ct);
                if (usage is null) { usage = new() { OwnerId=row.OwnerId,DomainId=row.DomainId,Month=month }; db.Add(usage); }
                checked { usage.ReceivedBytes += access.ReceivedBytes; usage.SentBytes += access.SentBytes; usage.Requests++; }
            }
            await db.SaveChangesAsync(ct);
            // Retention is triggered by real ingestion, never an idle polling loop.
            var prune = now >= nextRetention;
            if (prune)
                await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM ApacheLogRows WHERE Timestamp < {cutoff}",ct);
            await tx.CommitAsync(ct);
            if (prune) nextRetention = now.AddHours(1);
        }
        finally { writer.Release(); }
    }
}
