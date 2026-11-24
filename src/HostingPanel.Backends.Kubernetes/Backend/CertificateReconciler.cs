using HostingPanel.Api.Persistence;
using HostingPanel.Providers.CertManager;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;
namespace HostingPanel.Api;

// Only labelled Certificate resources are managed. cert-manager handles renewal.
public sealed class CertificateReconciler(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<CertificateReconciler> logger) : BackgroundService
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
        var backend = (KubernetesBackend)scope.ServiceProvider.GetRequiredService<IHostingBackend>();
        var settings = await backend.GetEffectiveSettingsAsync(ct);
        var domains = await db.Domains.AsNoTracking().ToListAsync(ct);
        var owners = await db.Accounts.AsNoTracking().ToDictionaryAsync(x => x.Id, ct);
        var client = KubernetesBackend.CertificateClient(configuration);
        using var actual = JsonDocument.Parse(await client.ListAsync(ct));
        var keep = new HashSet<string>(StringComparer.Ordinal);
        foreach (var domain in domains)
        {
            var acme = await KubernetesBackend.DomainAcmeAsync(db, domain.Id, ct);
            if (!acme.Enabled) continue;
            keep.Add(CertManagerClient.Name(domain.Id));
            var issuer = KubernetesBackend.ResolveIssuer(settings, owners[domain.OwnerId], acme);
            var names = KubernetesBackend.CertificateNames(domain, acme);
            if (issuer is null || names.Length == 0 || (acme.PreferWildcard && !issuer.SupportsWildcard)) continue;
            try { await client.ApplyAsync(domain.Id, names, issuer.IssuerName, issuer.IssuerKind, acme.KeyType, ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { logger.LogWarning(ex, "Certificate request failed for {Id}", domain.Id); }
        }
        foreach (var item in actual.RootElement.GetProperty("items").EnumerateArray())
        {
            var name = item.GetProperty("metadata").GetProperty("name").GetString()!;
            if (name.StartsWith("site-", StringComparison.Ordinal) && Guid.TryParseExact(name[5..], "N", out _) && !keep.Contains(name))
                await client.DeleteAsync(name, ct);
        }
    }
}
