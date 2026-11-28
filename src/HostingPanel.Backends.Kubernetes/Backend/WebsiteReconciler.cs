using HostingPanel.Providers.CertManager;
using System.Text.Json;
using HostingPanel.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HostingPanel.Api;

// A single active reconciler per SQLite database. Uses the Kubernetes SDK.
public sealed class WebsiteReconciler(IServiceScopeFactory scopes, IConfiguration configuration,
    ILogger<WebsiteReconciler> logger, NfsDirectoryProvisioner storage, HostingOperationNotifications notifications, KubernetesResourceClient resources) : BackgroundService
{
    private readonly NfsReconciliation nfs = new(storage);
    private readonly PhpRuntimeReconciliation php = new(configuration, resources);
    private readonly ApacheReconciliation apache = new(configuration, resources, logger);
    private bool migrated;
    private int runtimeDrift;
    private int routeDrift;
    private int apacheDrift;
    private bool legacyApacheRetired;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!configuration.GetValue<bool>("KubernetesHosting:Enabled")) return;
        await ReconciliationLoop.RunAsync(notifications.Reader(ReconciliationTarget.Website),
            (changed, token) => WatchResourcesAsync(changed, token),
            ReconcileAsync, logger, Timeout.InfiniteTimeSpan, ct, signal: () => notifications.Signal(ReconciliationTarget.Website));
    }

    private async Task WatchResourcesAsync(Action changed, CancellationToken ct)
    {
        void PhpChanged() { Interlocked.Exchange(ref runtimeDrift, 1); changed(); }
        void RouteChanged() { Interlocked.Exchange(ref routeDrift, 1); changed(); }
        await Task.WhenAll(
            resources.WatchApachePodsAsync(Namespace, () => { Interlocked.Exchange(ref apacheDrift, 1); changed(); }, ct),
            resources.WatchAccountPhpAsync(Namespace, PhpChanged, ct),
            resources.WatchWebsiteRoutesAsync(Namespace, RouteChanged, ct));
    }

    private async Task<bool> ReconcileAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref apacheDrift, 0) != 0) apache.Invalidate();
        if (Interlocked.Exchange(ref runtimeDrift, 0) != 0) php.InvalidateAll();
        if (Interlocked.Exchange(ref routeDrift, 0) != 0) migrated = false;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HostingPanelDbContext>();
        var rows = await db.ReconciliationOperations.Where(x => x.ResourceType == "website"
            && x.Status == "pending").ToListAsync(ct);
        var domains = await db.Domains.AsNoTracking().ToListAsync(ct);
        var identities = await PhpIdentityModel.AllocateAsync(db, domains.Select(x => x.OwnerId), ct);
        var ownerIds = domains.Select(d => d.OwnerId).Distinct().ToArray();
        var accounts = await db.Accounts.AsNoTracking().Where(x => ownerIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, ct);
        var (usable, storageErrors) = await nfs.EnsureAsync(domains, accounts, identities, ct);
        if (configuration.GetValue<bool>("ZfsQuota:Enabled"))
        {
            var quota = new ZfsAccountQuota(configuration);
            var quotaPackages = await db.Packages.AsNoTracking().ToDictionaryAsync(x => x.Id, ct);
            foreach (var group in usable.GroupBy(x => x.OwnerId).ToArray())
            {
                try
                {
                    var owner = accounts[group.Key];
                    var package = owner.PackageId is { } pid ? quotaPackages.GetValueOrDefault(pid)?.LimitsJson : null;
                    await quota.ApplyAsync(owner.Username, identities[group.Key].Uid,
                        ZfsAccountQuota.Resolve(package, owner.LimitsJson, "quota"),
                        ZfsAccountQuota.Resolve(package, owner.LimitsJson, "inode"), ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    foreach (var domain in group) storageErrors[domain.Id] = ex;
                }
            }
            usable.RemoveAll(x => storageErrors.ContainsKey(x.Id));
        }
        foreach (var (id, error) in storageErrors) logger.LogWarning(error, "Storage unavailable for website {Id}", id);
        var completed = new List<ReconciliationOperation>();
        var byId = rows.GroupBy(x => x.ResourceId).ToDictionary(x => x.Key, x => x.ToArray());
        var targets = byId.Keys.Concat(migrated ? Array.Empty<Guid>() : domains.Select(x => x.Id)).Distinct();
        var migrationFailed = false;
        foreach (var id in targets)
        {
            var group = byId.GetValueOrDefault(id) ?? [];
            var latest = group.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).FirstOrDefault();
            try
            {
                var domain = await db.Domains.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
                var name = "site-" + id.ToString("N");
                if (domain is null)
                    {
                    await resources.DeleteAsync("gateway.networking.k8s.io", "v1", Namespace, "httproutes", name, ct);
                }
                else
                {
                    if (storageErrors.TryGetValue(domain.Id, out var storageError))
                        throw new InvalidOperationException("Website storage unavailable.", storageError);
                    var manifest = BuildManifest(domain);
                    using var desired = JsonDocument.Parse(manifest);
                    var existing = await resources.TryGetAsync("gateway.networking.k8s.io", "v1", Namespace, "httproutes", name, ct);
                    if (existing is null || !RouteMatches(desired.RootElement.GetProperty("items")[0], existing))
                        await resources.ApplyAsync(manifest, Namespace, "hostingpanel", ct);
                }
                completed.AddRange(group);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                migrationFailed = true;
                if (latest is not null)
                {
                    latest.Attempts++;
                    latest.LastError = "Website reconciliation failed; see application log.";
                }
                logger.LogWarning(ex, "Could not reconcile website {Id}", id);
            }
            await db.SaveChangesAsync(ct);
            if (latest?.LastError is not null && latest.Attempts == 1) notifications.Publish();
        }
        var backendSettings = await scope.ServiceProvider.GetRequiredService<IKubernetesSettingsReader>().GetEffectiveSettingsAsync(ct);
        var packageIds = accounts.Values.Where(x => x.PackageId.HasValue).Select(x => x.PackageId!.Value).Distinct().ToArray();
        var packages = await db.Packages.AsNoTracking().Where(x => packageIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var desiredPhp = await php.EnsureAsync(Namespace, usable.Where(HasPhp)
            .Select(domain => (accounts[domain.OwnerId].Username, identities[domain.OwnerId], PhpRuntimePolicy.Resolve(backendSettings,
                accounts[domain.OwnerId].PackageId is { } packageId ? packages.GetValueOrDefault(packageId)?.LimitsJson : null,
                accounts[domain.OwnerId].LimitsJson))), ct);
        try
        {
            await apache.ActivateAsync(domains, accounts, identities, storageErrors, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            apache.Invalidate();
            foreach (var row in completed)
            {
                row.Attempts++;
                row.LastError = "Apache configuration activation failed; see application log.";
            }
            await db.SaveChangesAsync(ct);
            if (completed.Any(row => row.Attempts == 1)) notifications.Publish();
            logger.LogWarning(ex, "Apache configuration activation failed; will retry");
            return true;
        }
        // Keep obsolete runtimes until the new Apache configuration has been activated.
        await php.RetireAsync(Namespace, desiredPhp, ct);
        // Only remove obsolete transport objects after the NFS activation succeeded.
        if (!legacyApacheRetired)
        {
            try
            {
                await resources.RetireLegacyApacheConfigMapsAsync(Namespace, ct);
                legacyApacheRetired = true;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Legacy Apache ConfigMap retirement failed; will retry");
            }
        }
        // Legacy per-site deployments are retired explicitly by the administrator after testing.
        migrated = !migrationFailed;
        foreach (var row in completed)
        {
            row.Status = "completed";
            row.CompletedAt = DateTimeOffset.UtcNow;
            row.LastError = null;
        }
        await db.SaveChangesAsync(ct);
        if (completed.Count > 0) notifications.Publish();
        var cleanupRetry = await CleanupDeletedDomainsAsync(db, ct);
        return cleanupRetry || migrationFailed || !legacyApacheRetired || await db.ReconciliationOperations.AnyAsync(x =>
            x.Status == "pending" && x.ResourceType == "website", ct);
    }

    private async Task<bool> CleanupDeletedDomainsAsync(HostingPanelDbContext db, CancellationToken ct)
    {
        var retry = false;
        var pending = await db.ReconciliationOperations.Where(x => x.ResourceType == "domain-cleanup" && x.Status == "pending").ToListAsync(ct);
        foreach (var operation in pending)
        {
            try
            {
                using var payload = JsonDocument.Parse(operation.PayloadJson);
                var name = payload.RootElement.GetProperty("name").GetString()!;
                var username = payload.RootElement.GetProperty("username").GetString()!;
                var zoneIds = payload.RootElement.GetProperty("zoneIds").EnumerateArray().Select(x => x.GetGuid()).ToArray();
                if (await db.ReconciliationOperations.AnyAsync(x => x.Status == "pending" &&
                    ((x.ResourceType == "website" && x.ResourceId == operation.ResourceId) ||
                     (x.ResourceType == "dns-zone" && zoneIds.Contains(x.ResourceId))), ct)) continue;
                if (await db.Domains.AnyAsync(x => x.Name == name, ct)) throw new InvalidOperationException("Domain still exists.");
                var marker = await db.Configuration.FindAsync(["DeletingDomain:" + name], ct);
                if (marker is null) throw new InvalidOperationException("Missing deletion authorization.");
                if (configuration.GetValue<bool>("CertManager:Enabled"))
                {
                    var certName = HostingPanel.Providers.CertManager.CertManagerClient.Name(operation.ResourceId);
                    if (await resources.TryGetAsync("cert-manager.io", "v1", Namespace, "certificates", certName, ct) is not null)
                        continue;
                    var routeName = "hp-tls-" + operation.ResourceId.ToString("N");
                    await resources.DeleteAsync("gateway.networking.k8s.io", "v1", Namespace, "httproutes", routeName, ct);
                    // TLS Secrets are removed by cert-manager's certificate owner reference
                    // when enabled by the cluster administrator.
                }
                await nfs.DeleteAsync(operation.ResourceId, username, name, ct);
                db.Configuration.Remove(marker);
                operation.Status = "completed";
                operation.CompletedAt = DateTimeOffset.UtcNow;
                operation.LastError = null;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                retry = true;
                operation.Attempts++;
                operation.LastError = "Domain deletion incomplete; see application log.";
                logger.LogWarning(ex, "Domain cleanup failed for {Id}", operation.ResourceId);
            }
            await db.SaveChangesAsync(ct);
            if (operation.Status == "completed" || operation.Attempts == 1) notifications.Publish();
        }
        return retry;
    }

    private string Required(string key) => configuration["KubernetesHosting:" + key] is { Length: > 0 } value
        ? value : throw new InvalidOperationException("Missing KubernetesHosting:" + key);

    private string Namespace => configuration["KubernetesHosting:Namespace"] ?? "hosting";

    internal static (string Apache, bool Tls) BuildVirtualHost(HostedDomain domain, string username, PhpAccountIdentity identity, bool durableAccounting = false)
    {
        var host = domain.Name;
        if (host.Length > 253 || host.Split('.').Any(label => label.Length is < 1 or > 63
            || !char.IsAsciiLetterOrDigit(label[0]) || !char.IsAsciiLetterOrDigit(label[^1])
            || label.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')))
            throw new InvalidOperationException("Invalid hostname");
        using var doc = JsonDocument.Parse(domain.SettingsJson);
        bool Flag(string key, bool fallback) => doc.RootElement.TryGetProperty(key, out var value)
            ? value.ValueKind switch {
                JsonValueKind.True => true, JsonValueKind.False => false,
                JsonValueKind.String when value.GetString() is "ON" or "True" or "true" => true,
                JsonValueKind.String when value.GetString() is "OFF" or "False" or "false" => false,
                _ => fallback
            } : fallback;
        var php = Flag("php", true);
        var tls = Flag("ssl", true);
        if (Flag("cgi", false)) throw new InvalidOperationException("CGI is not supported");
        var siteRoot = "/home/" + username + "/domains/" + host;
        var root = siteRoot + "/public_html";
        // Only the canonical hostname is served; aliases require explicit routing.
        // Keep executable extensions and sensitive metadata out of static responses.
        var deniedPhp = php
            ? "(?i)^(?!.*(?-i:[.]php)$).*[.](?:php[0-9]*|phtml|phar|phps|inc)(?:[.]|$)"
            : "(?i)[.](?:php[0-9]*|phtml|phar|phps|inc)(?:[.]|$)";
        var apache = "<VirtualHost *:8080>\nServerName " + host + "\n" + ApacheAccessAccounting.Directive(domain.OwnerId, domain.Id, durableAccounting) + "\n" + ApacheAccessAccounting.ErrorDirective(domain.OwnerId, domain.Id, durableAccounting) + "\nDocumentRoot \"" + root + "\"" +
            "\n<Directory \"" + root + "\">\nOptions -Indexes -ExecCGI -Includes -FollowSymLinks -SymLinksIfOwnerMatch\nAllowOverride None\nRequire all granted\nDirectoryIndex " +
            (php ? "index.php index.html" : "index.html") + "\n</Directory>\n" +
            "<DirectoryMatch \"/[.](?!well-known(?:/|$))[^/]+(?:/|$)\">\nRequire all denied\n</DirectoryMatch>\n" +
            "<FilesMatch \"(?i)(?:^[.]|[.](?:bak|backup|old|orig|swp|sql|ini|log|dist|save)$|~$|^(?:composer[.](?:json|lock)|wp-config[.]php)$)\">\nRequire all denied\n</FilesMatch>\n" +
            "<FilesMatch \"" + deniedPhp + "\">\nRequire all denied\n</FilesMatch>\n" +
            (php ? "<FilesMatch \"[.]php$\">\nSetHandler \"proxy:fcgi://" + AccountPhpRuntime.Name(identity.AccountId) + ":9000" + "\"\n</FilesMatch>\n" : "") +
            "</VirtualHost>\n";
        return (apache, tls);
    }

    private static bool RouteMatches(JsonElement desired, string existing)
    {
        using var observed = JsonDocument.Parse(existing);
        return CertificateReconciler.MatchesDesired(desired.GetProperty("spec"), observed.RootElement.GetProperty("spec"));
    }

    private string BuildManifest(HostedDomain domain)
    {
        var host = domain.Name;
        var name = "site-" + domain.Id.ToString("N");
        var resources = new object[] {
            new { apiVersion = "gateway.networking.k8s.io/v1", kind = "HTTPRoute", metadata = new { name, @namespace = Namespace }, spec = new {
                parentRefs = new[] { new { name = Required("GatewayName"), @namespace = Required("GatewayNamespace"),
                    sectionName = Required("HttpListener") } },
                hostnames = new[] { host }, rules = new[] { new { backendRefs = new[] { new { name = "apache-baseline", port = 80 } } } }
            } }
        };
        return JsonSerializer.Serialize(new { apiVersion = "v1", kind = "List", items = resources });
    }

    private static bool HasPhp(HostedDomain domain)
    {
        using var doc = JsonDocument.Parse(domain.SettingsJson);
        return !doc.RootElement.TryGetProperty("php", out var flag) || (flag.ValueKind != JsonValueKind.False && !(flag.ValueKind == JsonValueKind.String && flag.GetString() is "OFF" or "False" or "false"));
    }

}
