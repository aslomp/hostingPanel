using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HostingPanel.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HostingPanel.Api;

// A single active reconciler per SQLite database. kubectl uses the configured
// kubeconfig or the in-cluster service account; no shell interpolation.
public sealed class WebsiteReconciler(IServiceScopeFactory scopes, IConfiguration configuration,
    ILogger<WebsiteReconciler> logger, NfsDirectoryProvisioner storage, HostingOperationNotifications notifications) : BackgroundService
{
    private string? appliedRuntimeHash;
    private bool migrated;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!configuration.GetValue<bool>("KubernetesHosting:Enabled")) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        do
        {
            try { await ReconcileAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogError(ex, "Website reconciliation failed"); }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HostingPanelDbContext>();
        var rows = await db.ReconciliationOperations.Where(x => x.ResourceType == "website"
            && x.Status == "pending").ToListAsync(ct);
        var shared = configuration.GetValue<bool>("KubernetesHosting:AllowSharedPhpRuntime");
        if (rows.Count == 0 && !shared && migrated) return;
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
                    await KubectlAsync(null, ct, "delete", "httproute,configmap", name, "--ignore-not-found=true");
                else
                {
                    if (storageErrors.TryGetValue(domain.Id, out var storageError))
                        throw new InvalidOperationException("Website storage unavailable.", storageError);
                    var manifest = BuildManifest(domain, accounts[domain.OwnerId].Username, identities[domain.OwnerId]);
                    await KubectlAsync(manifest, ct, "apply", "--server-side", "--field-manager=hostingpanel", "-f", "-");
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
                await KubectlAsync(manifest, ct, "apply", "--server-side", "--field-manager=hostingpanel", "-f", "-");
                appliedRuntimeHash = hash;
            }
        }
        // Legacy deployments are retired explicitly by the administrator after testing.
        migrated = !migrationFailed;
        foreach (var row in completed)
        {
            row.Status = "completed";
            row.CompletedAt = DateTimeOffset.UtcNow;
            row.LastError = null;
        }
        await db.SaveChangesAsync(ct);
        if (completed.Count > 0) notifications.Publish();
    }

    private string Required(string key) => configuration["KubernetesHosting:" + key] is { Length: > 0 } value
        ? value : throw new InvalidOperationException("Missing KubernetesHosting:" + key);

    private string Namespace => configuration["KubernetesHosting:Namespace"] ?? "hosting";

    private string BuildManifest(HostedDomain domain, string username, PhpAccountIdentity identity)
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
        var name = "site-" + domain.Id.ToString("N");
        var siteRoot = "/home/" + username + "/domains/" + host;
        var root = siteRoot + "/public_html";
        var phpVersion = PhpVersion(domain);
        if (php && !configuration.GetValue<bool>("KubernetesHosting:AllowSharedPhpRuntime"))
            throw new InvalidOperationException("Shared PHP requires explicit trust in all hosted PHP applications.");
        if (phpVersion is not ("7" or "8")) throw new InvalidOperationException("Unsupported PHP version");
        if (php && phpVersion == "7" && !configuration.GetValue<bool>("KubernetesHosting:EnablePhp7"))
            throw new InvalidOperationException("PHP 7 is disabled");
        var apache = "<VirtualHost *:8080>\nServerName " + host + "\nDocumentRoot \"" + root + "\"" +
            "\n<Directory \"" + root + "\">\nOptions -Indexes -ExecCGI -Includes -FollowSymLinks -SymLinksIfOwnerMatch\nAllowOverride None\nRequire all granted\nDirectoryIndex index.php index.html\n</Directory>\n" +
            (php ? "<FilesMatch \"[.]php$\">\nSetHandler \"proxy:fcgi://php-fpm-" + phpVersion + ":" + identity.Port + "\"\n</FilesMatch>\n"
            : "<FilesMatch \"[.]php$\">\nRequire all denied\n</FilesMatch>\n") + "</VirtualHost>\n";
        var configLabels = new Dictionary<string,string> { ["hostingpanel/component"] = "vhost" };
        var resources = new object[] {
            new { apiVersion = "v1", kind = "ConfigMap", metadata = new { name, @namespace = Namespace, labels = configLabels }, data = new Dictionary<string,string> { ["vhost.conf"] = apache } },
            new { apiVersion = "gateway.networking.k8s.io/v1", kind = "HTTPRoute", metadata = new { name, @namespace = Namespace }, spec = new {
                parentRefs = new[] { new { name = Required("GatewayName"), @namespace = Required("GatewayNamespace"),
                    sectionName = Required(tls ? "HttpsListener" : "HttpListener") } },
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
        var json = await KubectlAsync(null, ct, "get", "pods", "-l", "app.kubernetes.io/name=apache-baseline", "-o", "json");
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

    private async Task<string> KubectlAsync(string? manifest, CancellationToken ct, params string[] args)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        var start = new ProcessStartInfo(configuration["KubernetesHosting:KubectlPath"] ?? "kubectl") {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        start.ArgumentList.Add("--namespace");
        start.ArgumentList.Add(Namespace);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start kubectl");
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(token);
            var stderr = process.StandardError.ReadToEndAsync(token);
            if (manifest is not null) await process.StandardInput.WriteAsync(manifest.AsMemory(), token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(token);
            var output = await stdout;
            var error = await stderr;
            if (process.ExitCode != 0) throw new InvalidOperationException("kubectl failed: " + error);
            return output;
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }
}
