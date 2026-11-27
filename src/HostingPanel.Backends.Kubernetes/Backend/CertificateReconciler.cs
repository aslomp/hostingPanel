using HostingPanel.Api.Persistence;
using HostingPanel.Providers.CertManager;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;
namespace HostingPanel.Api;

// Only labelled Certificate resources are managed. cert-manager handles renewal.
public sealed class CertificateReconciler(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<CertificateReconciler> logger, KubernetesResourceClient resources, HostingOperationNotifications notifications, CertificateWork work) : BackgroundService
{
    private readonly Dictionary<Guid, string> ready = new();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!configuration.GetValue<bool>("CertManager:Enabled") || !configuration.GetValue<bool>("KubernetesHosting:Enabled")) return;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var ns = configuration["KubernetesHosting:Namespace"] ?? "hosting";
        var watches = new List<Task>
        {
            ReconciliationLoop.WatchAsync((_, token) => resources.WatchCertificateStateAsync(ns,
                () => { work.AllChanged(); notifications.Signal(ReconciliationTarget.Website); }, name => { if (TryDomainId(name, out var id)) { work.DomainChanged(id); notifications.Signal(ReconciliationTarget.Website); } },
                notifications.Publish, token), () => { }, logger, lifetime.Token)
        };
        if (configuration["CertManager:ListenerSetName"] is { Length: > 0 } set)
        {
            watches.Add(ReconciliationLoop.WatchAsync((_, token) => resources.WatchTlsRoutingAsync(ns,
                "listenersets", set, work.AllChanged, _ => work.AllChanged(), token), () => { }, logger, lifetime.Token));
            watches.Add(ReconciliationLoop.WatchAsync((_, token) => resources.WatchTlsRoutingAsync(ns,
                "httproutes", null, work.AllChanged, name =>
                {
                    if (TryTlsDomainId(name, out var id)) work.DomainChanged(id);
                }, token), () => { }, logger, lifetime.Token));
        }
        try
        {
            await ReconciliationLoop.RunAsync(work.Wake.Reader(ReconciliationTarget.Certificate), null,
                ReconcileAsync, logger, Timeout.InfiniteTimeSpan, ct, work.Recover);
        }
        finally
        {
            await lifetime.CancelAsync();
            await Task.WhenAll(watches);
        }
    }

    private async Task<bool> ReconcileAsync(CancellationToken ct)
    {
        var keys = work.Take();
        // A duplicate wake can race with Take; it must not trigger a full pass.
        if (keys is { Length: 0 }) return false;
        try { return await ReconcileCoreAsync(keys, ct); }
        catch { work.Recover(); throw; }
    }

    internal static bool TryTlsDomainId(string name, out Guid id)
    {
        id = default;
        return name.StartsWith("hp-tls-", StringComparison.Ordinal) &&
            Guid.TryParseExact(name[7..], "N", out id);
    }

    internal static bool TryDomainId(string name, out Guid id)
    {
        id = default;
        return name.StartsWith("site-", StringComparison.Ordinal) &&
            Guid.TryParseExact(name[5..], "N", out id);
    }

    private async Task<bool> ReconcileCoreAsync(Guid[]? keys, CancellationToken ct)
    {
        var retry = false;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HostingPanelDbContext>();
        var backend = scope.ServiceProvider.GetRequiredService<IKubernetesSettingsReader>();
        var settings = await backend.GetEffectiveSettingsAsync(ct);
        var ns = configuration["KubernetesHosting:Namespace"] ?? "hosting";
        var query = db.Domains.AsNoTracking();
        if (keys is not null) query = query.Where(x => keys.Contains(x.Id));
        var domains = await query.ToListAsync(ct);
        var ownerIds = domains.Select(x => x.OwnerId).Distinct().ToArray();
        var owners = await db.Accounts.AsNoTracking().Where(x => ownerIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var client = new CertManagerClient(resources, ns);
        var certificates = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (keys is null)
        {
            using var actual = JsonDocument.Parse(await client.ListAsync(ct));
            foreach (var item in actual.RootElement.GetProperty("items").EnumerateArray())
                certificates.Add(item.GetProperty("metadata").GetProperty("name").GetString()!, item.Clone());
        }
        else
        {
            foreach (var id in keys)
            {
                var name = CertManagerClient.Name(id);
                var actual = await resources.TryGetAsync("cert-manager.io", "v1", ns, "certificates", name, ct);
                if (actual is null) continue;
                using var doc = JsonDocument.Parse(actual);
                var item = doc.RootElement;
                if (!item.GetProperty("metadata").TryGetProperty("labels", out var labels) ||
                    !labels.TryGetProperty("hostingpanel/component", out var label) || label.GetString() != "certificate")
                    throw new InvalidOperationException("Refusing to manage an unowned Certificate.");
                certificates.Add(name, item.Clone());
            }
        }
        var keep = new HashSet<string>(StringComparer.Ordinal);
        var next = keys is null ? new Dictionary<Guid, string>() : new Dictionary<Guid, string>(ready);
        if (keys is not null) foreach (var id in keys) next.Remove(id);
        foreach (var domain in domains)
        {
            var acme = await KubernetesCertificateProvider.DomainAcmeAsync(db, domain.Id, ct);
            if (!acme.Enabled || !SslEnabled(domain)) continue;
            keep.Add(CertManagerClient.Name(domain.Id));
            var issuer = KubernetesCertificateProvider.ResolveIssuer(settings, owners[domain.OwnerId], acme);
            var names = KubernetesCertificateProvider.CertificateNames(domain, acme);
            if (issuer is null || names.Length == 0 || (acme.PreferWildcard && !issuer.SupportsWildcard)) continue;
            try {
                var name = CertManagerClient.Name(domain.Id);
                using var desired = JsonDocument.Parse(client.BuildManifest(domain.Id, names, issuer.IssuerName, issuer.IssuerKind, acme.KeyType));
                if (!certificates.TryGetValue(name, out var observed) ||
                    !MatchesDesired(desired.RootElement.GetProperty("spec"), observed.GetProperty("spec")))
                    await resources.ApplyAsync(desired.RootElement.GetRawText(), configuration["KubernetesHosting:Namespace"] ?? "hosting", "hostingpanel-certificate", ct);
                else if (IsReady(observed) && names.Contains(domain.Name))
                    next[domain.Id] = domain.Name;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested) { retry = true; if (ready.TryGetValue(domain.Id, out var previous)) next[domain.Id] = previous; logger.LogWarning(ex, "Certificate request failed for {Id}", domain.Id); }
        }
        var set = configuration["CertManager:ListenerSetName"];
        if (!string.IsNullOrEmpty(set))
        {
            if (settings.TlsListenerSetName.Length > 0 && settings.TlsListenerSetName != set)
                throw new InvalidOperationException("ListenerSet must match the Helm-authorized name.");
            await new ListenerSetReconciler(resources, configuration["KubernetesHosting:Namespace"] ?? "hosting")
                .ReconcileAsync(set, configuration["KubernetesHosting:GatewayName"] ?? throw new InvalidOperationException("Missing GatewayName."),
                    configuration["KubernetesHosting:GatewayNamespace"] ?? throw new InvalidOperationException("Missing GatewayNamespace."), next.Select(x => (x.Key, x.Value)).ToArray(), ct, keys);
        }
        var removed = false;
        foreach (var (name, item) in certificates)
        {
            if (TryDomainId(name, out _) && !keep.Contains(name))
            {
                await resources.DeleteObservedAsync("cert-manager.io", "v1", ns, "certificates", item, ct);
                removed = true;
            }
        }
        var changed = ready.Count != next.Count || ready.Any(x => !next.TryGetValue(x.Key, out var host) || host != x.Value);
        ready.Clear();
        foreach (var pair in next) ready.Add(pair.Key, pair.Value);
        if (changed) notifications.Publish();
        if (changed || removed) notifications.Signal(ReconciliationTarget.Website);
        if (retry) work.Recover();
        return retry;
    }

    // Compare only fields owned by HostingPanel; API defaults and other
    // controllers' fields must not cause repeated server-side apply.
    internal static bool MatchesDesired(JsonElement desired, JsonElement observed)
    {
        if (desired.ValueKind != observed.ValueKind) return false;
        if (desired.ValueKind == JsonValueKind.Object)
            return desired.EnumerateObject().All(p => observed.TryGetProperty(p.Name, out var value) && MatchesDesired(p.Value, value));
        if (desired.ValueKind == JsonValueKind.Array)
        {
            var a = desired.EnumerateArray().ToArray();
            var b = observed.EnumerateArray().ToArray();
            return a.Length == b.Length && a.Zip(b).All(pair => MatchesDesired(pair.First, pair.Second));
        }
        return JsonElement.DeepEquals(desired, observed);
    }

    internal static bool IsReady(JsonElement cert) =>
        cert.TryGetProperty("status", out var status) && status.TryGetProperty("conditions", out var conditions) &&
        conditions.EnumerateArray().Any(c => c.GetProperty("type").GetString() == "Ready" && c.GetProperty("status").GetString() == "True" &&
                (!c.TryGetProperty("observedGeneration", out var generation) ||
                 !cert.GetProperty("metadata").TryGetProperty("generation", out var current) ||
                 JsonElement.DeepEquals(generation, current)));
    private static bool SslEnabled(HostedDomain domain)
    {
        using var doc = JsonDocument.Parse(domain.SettingsJson);
        return !doc.RootElement.TryGetProperty("ssl", out var flag) ||
            (flag.ValueKind != JsonValueKind.False && !(flag.ValueKind == JsonValueKind.String && flag.GetString() is "OFF" or "False" or "false"));
    }
}
