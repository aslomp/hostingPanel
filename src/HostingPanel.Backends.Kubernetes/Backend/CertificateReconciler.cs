using HostingPanel.Api.Persistence;
using HostingPanel.Providers.CertManager;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;
namespace HostingPanel.Api;

// Only labelled Certificate resources are managed. cert-manager handles renewal.
public sealed class CertificateReconciler(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<CertificateReconciler> logger, KubernetesResourceClient resources) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!configuration.GetValue<bool>("CertManager:Enabled") || !configuration.GetValue<bool>("KubernetesHosting:Enabled")) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try { await ReconcileAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning(ex, "Certificate reconciliation failed"); }
        } while (await timer.WaitForNextTickAsync(ct));
    }
    private async Task ReconcileAsync(CancellationToken ct)
    {
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
        var readyCertificates = actual.RootElement.GetProperty("items").EnumerateArray().Where(IsReady).Select(x => x.GetProperty("metadata").GetProperty("name").GetString()).ToHashSet();
        foreach (var domain in domains)
        {
            var acme = await KubernetesCertificateProvider.DomainAcmeAsync(db, domain.Id, ct);
            if (!acme.Enabled || !SslEnabled(domain)) continue;
            keep.Add(CertManagerClient.Name(domain.Id));
            var issuer = KubernetesCertificateProvider.ResolveIssuer(settings, owners[domain.OwnerId], acme);
            var names = KubernetesCertificateProvider.CertificateNames(domain, acme);
            if (issuer is null || names.Length == 0 || (acme.PreferWildcard && !issuer.SupportsWildcard)) continue;
            try {
                await client.ApplyAsync(domain.Id, names, issuer.IssuerName, issuer.IssuerKind, acme.KeyType, ct);
                if (readyCertificates.Contains(CertManagerClient.Name(domain.Id)) && names.Contains(domain.Name))
                    readyListeners.Add((domain.Id, domain.Name));
            }
            catch (Exception ex) when (!ct.IsCancellationRequested) { logger.LogWarning(ex, "Certificate request failed for {Id}", domain.Id); }
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
    }
    private static bool IsReady(JsonElement cert) =>
        cert.TryGetProperty("status", out var status) && status.TryGetProperty("conditions", out var conditions) &&
        conditions.EnumerateArray().Any(c => c.GetProperty("type").GetString() == "Ready" && c.GetProperty("status").GetString() == "True");
    private static bool SslEnabled(HostedDomain domain)
    {
        using var doc = JsonDocument.Parse(domain.SettingsJson);
        return !doc.RootElement.TryGetProperty("ssl", out var flag) ||
            (flag.ValueKind != JsonValueKind.False && !(flag.ValueKind == JsonValueKind.String && flag.GetString() is "OFF" or "False" or "false"));
    }
}
