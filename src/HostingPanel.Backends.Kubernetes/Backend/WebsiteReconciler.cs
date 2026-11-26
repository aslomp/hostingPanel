using HostingPanel.Providers.CertManager;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using HostingPanel.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HostingPanel.Api;

// A single active reconciler per SQLite database. Uses the Kubernetes SDK.
public sealed class WebsiteReconciler(IServiceScopeFactory scopes, IConfiguration configuration,
    ILogger<WebsiteReconciler> logger, NfsDirectoryProvisioner storage, HostingOperationNotifications notifications, KubernetesResourceClient resources) : BackgroundService
{
    private string? appliedRuntimeHash;
    private string? stagedApacheRevision;
    private string? activatedReplicaFingerprint;
    private bool migrated;
    private bool legacyApacheRetired;
    private string? appliedListenerSet;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!configuration.GetValue<bool>("KubernetesHosting:Enabled")) return;
        var wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true
        });
        void Notify() => wake.Writer.TryWrite(true);
        notifications.Changed += Notify;
        using var watchCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var watch = WatchApachePodsAsync(Notify, watchCancellation.Token);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var needsRetry = true;
                try { needsRetry = await ReconcileAsync(ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex) { logger.LogError(ex, "Website reconciliation failed"); }

                // Coalesce invalidations. Keep periodic recovery for failed durable work
                // and settings changes which do not publish notifications.
                using var delay = CancellationTokenSource.CreateLinkedTokenSource(ct);
                delay.CancelAfter(TimeSpan.FromSeconds(needsRetry ? 5 : 60));
                try { await wake.Reader.ReadAsync(delay.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                while (wake.Reader.TryRead(out _)) { }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            notifications.Changed -= Notify;
            await watchCancellation.CancelAsync();
            await watch;
        }
    }

    private async Task WatchApachePodsAsync(Action notify, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await resources.WatchApachePodsAsync(Namespace, notify, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning(ex, "Apache pod watch disconnected"); }
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        }
    }

    private async Task<bool> ReconcileAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HostingPanelDbContext>();
        var rows = await db.ReconciliationOperations.Where(x => x.ResourceType == "website"
            && x.Status == "pending").ToListAsync(ct);
        var shared = configuration.GetValue<bool>("KubernetesHosting:AllowSharedPhpRuntime");
        var settings = await scope.ServiceProvider.GetRequiredService<IKubernetesSettingsReader>().GetEffectiveSettingsAsync(ct);
        var listenerSet = settings.TlsListenerSetName;
        var listenerChanged = appliedListenerSet != listenerSet;
        var cleanupPending = await db.ReconciliationOperations.AnyAsync(x => x.ResourceType == "domain-cleanup" && x.Status == "pending", ct);
        var stagePending = stagedApacheRevision is null;
        var runtime = new ApacheRevisionRuntime(resources, Namespace);
        if (!stagePending && !shared && rows.Count == 0 && migrated && !listenerChanged && !cleanupPending)
            stagePending = activatedReplicaFingerprint != await runtime.GetReplicaFingerprintAsync(ct);
        if (rows.Count == 0 && !shared && migrated && !listenerChanged && !cleanupPending && !stagePending && legacyApacheRetired) return false;
        var domains = await db.Domains.AsNoTracking().ToListAsync(ct);
        var identities = await PhpIdentityModel.AllocateAsync(db, domains.Select(x => x.OwnerId), ct);
        var ownerIds = domains.Select(d => d.OwnerId).Distinct().ToArray();
        var accounts = await db.Accounts.AsNoTracking().Where(x => ownerIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, ct);
        // Resolve storage independently for each domain. A broken mount or
        // missing directory must not block unrelated websites or deletion.
        var usable = new List<HostedDomain>();
        var storageErrors = new Dictionary<Guid, Exception>();
        foreach (var domain in domains)
        {
            try
            {
                var owner = accounts[domain.OwnerId];
                var identity = identities[domain.OwnerId];
                var (uid, gid) = await storage.EnsureAsync(owner.Username, domain.Name, identity, ct);
                if (identity.ExistingUid is { } previousUid && (previousUid != uid || identity.ExistingGid != gid))
                    throw new InvalidOperationException("Inconsistent ownership of DirectAdmin account directories.");
                identity.ExistingUid = uid;
                identity.ExistingGid = gid;
                usable.Add(domain);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                storageErrors[domain.Id] = ex;
                logger.LogWarning(ex, "Storage unavailable for website {Id}", domain.Id);
            }
        }
        var completed = new List<ReconciliationOperation>();
        var byId = rows.GroupBy(x => x.ResourceId).ToDictionary(x => x.Key, x => x.ToArray());
        var targets = byId.Keys.Concat(migrated && !listenerChanged ? Array.Empty<Guid>() : domains.Select(x => x.Id)).Distinct();
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
                    var manifest = BuildManifest(domain, accounts[domain.OwnerId].Username, identities[domain.OwnerId], listenerSet.Length > 0);
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
        if (shared)
        {
            // FPM itself enforces source IPs, including connections originating
            // inside the same runtime pod where NetworkPolicy cannot help.
            var sources = await GetApacheSourcesAsync(usable, ct);
            var manifest = BuildRuntimeManifest(usable, identities, sources);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest)));
            if (hash != appliedRuntimeHash)
            {
                await resources.ApplyAsync(manifest, Namespace, "hostingpanel", ct);
                appliedRuntimeHash = hash;
            }
        }
        try
        {
            await ActivateApacheConfigurationAsync(domains, accounts, identities, storageErrors, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            stagedApacheRevision = null;
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
        if (!migrationFailed) appliedListenerSet = listenerSet;
        foreach (var row in completed)
        {
            row.Status = "completed";
            row.CompletedAt = DateTimeOffset.UtcNow;
            row.LastError = null;
        }
        await db.SaveChangesAsync(ct);
        if (completed.Count > 0) notifications.Publish();
        await CleanupDeletedDomainsAsync(db, ct);
        return migrationFailed || !legacyApacheRetired || await db.ReconciliationOperations.AnyAsync(x =>
            x.Status == "pending" && (x.ResourceType == "website" || x.ResourceType == "domain-cleanup"), ct);
    }

    private async Task ActivateApacheConfigurationAsync(
        IReadOnlyList<HostedDomain> domains,
        IReadOnlyDictionary<Guid, HostingAccount> accounts,
        IReadOnlyDictionary<Guid, PhpAccountIdentity> identities,
        IReadOnlyDictionary<Guid, Exception> storageErrors,
        CancellationToken ct)
    {
        if (storageErrors.Count != 0)
            throw new InvalidOperationException("Website storage unavailable; retaining previous Apache revision.");
        var revision = new ApacheConfigurationRevision(domains.Select(domain =>
            KeyValuePair.Create(domain.Id, BuildVirtualHost(domain,
                accounts[domain.OwnerId].Username, identities[domain.OwnerId]).Apache)));
        var store = new ApacheCandidateStore(Required("ApacheConfigPath"));
        var runtime = new ApacheRevisionRuntime(resources, Namespace);
        var fingerprint = await runtime.GetReplicaFingerprintAsync(ct);
        if (revision.Id == stagedApacheRevision && fingerprint == activatedReplicaFingerprint &&
            await store.ReadActiveAsync(ct) == revision.Id) return;
        await new ApacheRevisionActivator(store).ActivateAsync(revision,
            runtime.ValidateAsync, runtime.ReloadAsync, ct);
        if (fingerprint != await runtime.GetReplicaFingerprintAsync(ct))
            throw new InvalidOperationException("Apache replica set changed during activation; retry required.");
        stagedApacheRevision = revision.Id;
        activatedReplicaFingerprint = fingerprint;
        logger.LogInformation("Activated Apache configuration revision {Revision} with {Count} virtualhosts",
            revision.Id, revision.VirtualHosts.Count);
    }

    private async Task CleanupDeletedDomainsAsync(HostingPanelDbContext db, CancellationToken ct)
    {
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
                    var client = new CertManagerClient(resources, Namespace);
                    var certName = HostingPanel.Providers.CertManager.CertManagerClient.Name(operation.ResourceId);
                    using var certs = JsonDocument.Parse(await client.ListAsync(ct));
                    if (certs.RootElement.GetProperty("items").EnumerateArray().Any(x => x.GetProperty("metadata").GetProperty("name").GetString() == certName))
                        continue;
                    var routeName = "hp-tls-" + operation.ResourceId.ToString("N");
                    await resources.DeleteAsync("gateway.networking.k8s.io", "v1", Namespace, "httproutes", routeName, ct);
                    // TLS Secrets are removed by cert-manager's certificate owner reference
                    // when enabled by the cluster administrator.
                }
                await storage.DeleteAsync(username, name, ct);
                db.Configuration.Remove(marker);
                operation.Status = "completed";
                operation.CompletedAt = DateTimeOffset.UtcNow;
                operation.LastError = null;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                operation.Attempts++;
                operation.LastError = "Domain deletion incomplete; see application log.";
                logger.LogWarning(ex, "Domain cleanup failed for {Id}", operation.ResourceId);
            }
            await db.SaveChangesAsync(ct);
            notifications.Publish();
        }
    }

    private string Required(string key) => configuration["KubernetesHosting:" + key] is { Length: > 0 } value
        ? value : throw new InvalidOperationException("Missing KubernetesHosting:" + key);

    private string Namespace => configuration["KubernetesHosting:Namespace"] ?? "hosting";

    private (string Apache, bool Tls) BuildVirtualHost(HostedDomain domain, string username, PhpAccountIdentity identity)
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
        var phpVersion = PhpVersion(domain);
        if (php && !configuration.GetValue<bool>("KubernetesHosting:AllowSharedPhpRuntime"))
            throw new InvalidOperationException("Shared PHP requires explicit trust in all hosted PHP applications.");
        if (phpVersion is not ("7" or "8")) throw new InvalidOperationException("Unsupported PHP version");
        if (php && phpVersion == "7" && !configuration.GetValue<bool>("KubernetesHosting:EnablePhp7"))
            throw new InvalidOperationException("PHP 7 is disabled");
        // Only the canonical hostname is served; aliases require explicit routing.
        // Keep executable extensions and sensitive metadata out of static responses.
        var deniedPhp = php
            ? "(?i)^(?!.*(?-i:[.]php)$).*[.](?:php[0-9]*|phtml|phar|phps|inc)(?:[.]|$)"
            : "(?i)[.](?:php[0-9]*|phtml|phar|phps|inc)(?:[.]|$)";
        var apache = "<VirtualHost *:8080>\nServerName " + host + "\nDocumentRoot \"" + root + "\"" +
            "\n<Directory \"" + root + "\">\nOptions -Indexes -ExecCGI -Includes -FollowSymLinks -SymLinksIfOwnerMatch\nAllowOverride None\nRequire all granted\nDirectoryIndex " +
            (php ? "index.php index.html" : "index.html") + "\n</Directory>\n" +
            "<DirectoryMatch \"/[.](?!well-known(?:/|$))[^/]+(?:/|$)\">\nRequire all denied\n</DirectoryMatch>\n" +
            "<FilesMatch \"(?i)(?:^[.]|[.](?:bak|backup|old|orig|swp|sql|ini|log|dist|save)$|~$|^(?:composer[.](?:json|lock)|wp-config[.]php)$)\">\nRequire all denied\n</FilesMatch>\n" +
            "<FilesMatch \"" + deniedPhp + "\">\nRequire all denied\n</FilesMatch>\n" +
            (php ? "<FilesMatch \"[.]php$\">\nSetHandler \"proxy:fcgi://php-fpm-" + phpVersion + ":" + identity.Port + "\"\n</FilesMatch>\n" : "") +
            "</VirtualHost>\n";
        return (apache, tls);
    }

    private string BuildManifest(HostedDomain domain, string username, PhpAccountIdentity identity, bool managedTls)
    {
        var (_, tls) = BuildVirtualHost(domain, username, identity);
        var host = domain.Name;
        var name = "site-" + domain.Id.ToString("N");
        var resources = new object[] {
            new { apiVersion = "gateway.networking.k8s.io/v1", kind = "HTTPRoute", metadata = new { name, @namespace = Namespace }, spec = new {
                parentRefs = new[] { new { name = Required("GatewayName"), @namespace = Required("GatewayNamespace"),
                    sectionName = Required(tls && !managedTls ? "HttpsListener" : "HttpListener") } },
                hostnames = new[] { host }, rules = new[] { new { backendRefs = new[] { new { name = "apache-baseline", port = 80 } } } }
            } }
        };
        return JsonSerializer.Serialize(new { apiVersion = "v1", kind = "List", items = resources });
    }

    private string PhpVersion(HostedDomain domain) =>
        configuration["KubernetesHosting:SitePhpVersions:" + domain.Name]
        ?? configuration["KubernetesHosting:DefaultPhpVersion"] ?? "8";

    private static bool HasPhp(HostedDomain domain)
    {
        using var doc = JsonDocument.Parse(domain.SettingsJson);
        return !doc.RootElement.TryGetProperty("php", out var flag) || (flag.ValueKind != JsonValueKind.False && !(flag.ValueKind == JsonValueKind.String && flag.GetString() is "OFF" or "False" or "false"));
    }

    private string BuildRuntimeManifest(IReadOnlyList<HostedDomain> domains,
        IReadOnlyDictionary<Guid, PhpAccountIdentity> identities,
        IReadOnlyDictionary<Guid, string[]> sources)
    {
        var resources = new List<object>();
        foreach (var version in new[] { "8", "7" })
        {
            if (version == "7" && !configuration.GetValue<bool>("KubernetesHosting:EnablePhp7")) continue;
            var name = "php-fpm-" + version;
            var labels = new Dictionary<string,string> { ["app.kubernetes.io/name"] = name };
            object Meta() => new { name, @namespace = Namespace, labels };
            var owners = domains.Where(x => HasPhp(x) && PhpVersion(x) == version)
                .Select(x => x.OwnerId).Distinct().Order().Select(x => identities[x]).ToArray();
            var pools = new StringBuilder("[global]\nerror_log = /proc/self/fd/2\ndaemonize = no\n");
            var passwd = new StringBuilder();
            var group = new StringBuilder();
            if (owners.Length == 0)
                pools.Append("[idle]\nuser = www-data\ngroup = www-data\nlisten = 127.0.0.1:9000\nlisten.allowed_clients = 0.0.0.0\npm = ondemand\npm.max_children = 1\nclear_env = yes\nsecurity.limit_extensions = .php\n");
            foreach (var owner in owners)
            {
                passwd.AppendLine($"{owner.UnixName}:x:{owner.Uid}:{owner.Gid}:Hosting account:/nonexistent:/usr/sbin/nologin");
                group.AppendLine($"{owner.UnixName}:x:{owner.Gid}:");
                pools.AppendLine($"[{owner.UnixName}]");
                pools.AppendLine($"user = {owner.UnixName}");
                pools.AppendLine($"group = {owner.UnixName}");
                pools.AppendLine($"listen = 0.0.0.0:{owner.Port}");
                // An unset allowlist accepts all clients. Use a non-routable
                // source address when no matching Apache pod is available.
                var allowed = sources.GetValueOrDefault(owner.AccountId) ?? [];
                pools.AppendLine("listen.allowed_clients = " + (allowed.Length == 0 ? "0.0.0.0" : string.Join(",", allowed)));
                pools.AppendLine("pm = ondemand\npm.max_children = 8\npm.process_idle_timeout = 10s\nclear_env = yes\nsecurity.limit_extensions = .php");
            }
            var data = new Dictionary<string,string> { ["php-fpm.conf"] = pools.ToString(), ["passwd"] = passwd.ToString(), ["group"] = group.ToString() };
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data))));
            var image = version == "7" ? Required("Php7Image") : configuration["KubernetesHosting:Php8Image"] ?? "php:8.6.0-fpm";
            resources.Add(new { apiVersion = "v1", kind = "ConfigMap", metadata = Meta(), data });
            resources.Add(new { apiVersion = "apps/v1", kind = "Deployment", metadata = Meta(), spec = new {
                replicas = configuration.GetValue<int?>("KubernetesHosting:Php" + version + "Replicas") ?? 1,
                selector = new { matchLabels = labels }, template = new {
                    metadata = new { labels, annotations = new Dictionary<string,string> { ["hostingpanel/config-sha256"] = hash } }, spec = new {
                        automountServiceAccountToken = false,
                        containers = new[] { new { name = "php-fpm", image,
                            command = new[] { "/bin/sh", "-ec" },
                            args = new[] { "cat /etc/hostingpanel/passwd >> /etc/passwd; cat /etc/hostingpanel/group >> /etc/group; php-fpm -t -y /etc/hostingpanel/php-fpm.conf; exec php-fpm -F -y /etc/hostingpanel/php-fpm.conf" },
                            securityContext = new { runAsUser = 0, allowPrivilegeEscalation = false,
                                capabilities = new { drop = new[] { "ALL" }, add = new[] { "SETUID", "SETGID", "CHOWN", "KILL", "DAC_OVERRIDE" } } },
                            readinessProbe = owners.Length == 0
                                ? (object)new { exec = new { command = new[] { "php", "-r", "exit(@fsockopen('127.0.0.1',9000)?0:1);" } } }
                                : new { tcpSocket = new { port = owners[0].Port } },
                            volumeMounts = new object[] { new { name = "sites", mountPath = "/home" },
                                new { name = "config", mountPath = "/etc/hostingpanel", readOnly = true } } } },
                        volumes = new object[] { new { name = "sites", nfs = new { server = Required("NfsServer"), path = Required("NfsPath") } },
                            new { name = "config", configMap = new { name } } }
                    }
                }
            } });
            resources.Add(new { apiVersion = "networking.k8s.io/v1", kind = "NetworkPolicy", metadata = Meta(), spec = new {
                podSelector = new { matchLabels = labels }, policyTypes = new[] { "Ingress" },
                ingress = owners.Select(owner => new {
                    from = new[] { new { podSelector = new { matchLabels = new Dictionary<string,string> {
                        ["app.kubernetes.io/name"] = "apache-baseline" } } } },
                    ports = new[] { new { protocol = "TCP", port = owner.Port } }
                }).ToArray()
            } });
            // One service per version, with one named TCP port per account.
            var ports = owners.Select(owner => new { name = owner.UnixName, port = owner.Port, targetPort = owner.Port }).ToList();
            // Kubernetes requires a nonempty service port list.
            if (ports.Count == 0) ports.Add(new { name = "health", port = 9000, targetPort = 9000 });
            resources.Add(new { apiVersion = "v1", kind = "Service", metadata = Meta(), spec = new {
                type = "ClusterIP", selector = labels, ports
            } });
        }
        return JsonSerializer.Serialize(new { apiVersion = "v1", kind = "List", items = resources });
    }

    private async Task<Dictionary<Guid, string[]>> GetApacheSourcesAsync(
        IReadOnlyList<HostedDomain> domains, CancellationToken ct)
    {
        var json = await resources.ListPodsAsync(Namespace, "app.kubernetes.io/name=apache-baseline", ct);
        using var doc = JsonDocument.Parse(json);
        var sources = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var pod in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            if (pod.GetProperty("metadata").TryGetProperty("deletionTimestamp", out _) ||
                !pod.TryGetProperty("status", out var status) ||
                !status.TryGetProperty("podIP", out var address) ||
                !IPAddress.TryParse(address.GetString(), out var ip) ||
                ip.AddressFamily != AddressFamily.InterNetwork ||
                IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any)) continue;
            sources.Add(ip.ToString());
        }
        return domains.Select(x => x.OwnerId).Distinct().ToDictionary(x => x, _ => sources.ToArray());
    }

}
