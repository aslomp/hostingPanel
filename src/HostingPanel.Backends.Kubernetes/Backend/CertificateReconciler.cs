using HostingPanel.Api.Persistence;
using HostingPanel.Providers.CertManager;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;
namespace HostingPanel.Api;

// Only labelled Certificate resources are managed. cert-manager handles renewal.
public sealed class CertificateReconciler(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<CertificateReconciler> logger, KubernetesResourceClient resources, HostingOperationNotifications notifications) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!configuration.GetValue<bool>("CertManager:Enabled") || !configuration.GetValue<bool>("KubernetesHosting:Enabled")) return;
        await ReconciliationLoop.RunAsync(notifications,
            (changed, token) => resources.WatchCustomResourcesAsync("cert-manager.io", "v1",
                configuration["KubernetesHosting:Namespace"] ?? "hosting", "certificates",
                "hostingpanel/component=certificate", changed, token),
            ReconcileAsync, logger, TimeSpan.FromHours(1), ct);
    }

    private async Task<bool> ReconcileAsync(CancellationToken ct)
    {
        var retry = false;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HostingPanelDbContext>();
        var backend = scope.ServiceProvider.GetRequiredService<IKubernetesSettingsReader>();
        var settings = await backend.GetEffectiveSettingsAsync(ct);
        var domains = await db.Domains.AsNoTracking().ToListAsync(ct);
        var owners = await db.Accounts.AsNoTracking().ToDictionaryAsync(x => x.Id, ct);
        var client = new CertManagerClient(resources, configuration["KubernetesHosting:Namespace"] ?? "hosting");
        using var actual = JsonDocument.Parse(await client.ListAsync(ct));
        var keep = new HashSet<string>(StringComparer.Ordinal);
        var readyListeners = new List<(Guid Id, string Host)>();
        var certificates = actual.RootElement.GetProperty("items").EnumerateArray()
            .ToDictionary(x => x.GetProperty("metadata").GetProperty("name").GetString()!, StringComparer.Ordinal);
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
                    readyListeners.Add((domain.Id, domain.Name));
            }
            catch (Exception ex) when (!ct.IsCancellationRequested) { retry = true; logger.LogWarning(ex, "Certificate request failed for {Id}", domain.Id); }
        }
        var set = configuration["CertManager:ListenerSetName"];
        if (!string.IsNullOrEmpty(set))
        {
            if (settings.TlsListenerSetName.Length > 0 && settings.TlsListenerSetName != set)
                throw new InvalidOperationException("ListenerSet must match the Helm-authorized name.");
            await new ListenerSetReconciler(resources, configuration["KubernetesHosting:Namespace"] ?? "hosting")
                .ReconcileAsync(set, configuration["KubernetesHosting:GatewayName"] ?? throw new InvalidOperationException("Missing GatewayName."),
                    configuration["KubernetesHosting:GatewayNamespace"] ?? throw new InvalidOperationException("Missing GatewayNamespace."), readyListeners, ct);
        }
        foreach (var item in actual.RootElement.GetProperty("items").EnumerateArray())
        {
            var name = item.GetProperty("metadata").GetProperty("name").GetString()!;
            if (name.StartsWith("site-", StringComparison.Ordinal) && Guid.TryParseExact(name[5..], "N", out _) && !keep.Contains(name))
                await client.DeleteAsync(name, ct);
        }
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
