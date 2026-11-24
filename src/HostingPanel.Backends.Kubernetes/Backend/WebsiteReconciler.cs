using System.Diagnostics;
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
    ILogger<WebsiteReconciler> logger) : BackgroundService
{
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
        if (rows.Count == 0) return;
        await KubectlAsync(BuildRuntimeManifest(), ct, "apply", "--server-side", "--field-manager=hostingpanel", "-f", "-");
        foreach (var group in rows.GroupBy(x => x.ResourceId))
        {
            var latest = group.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).First();
            try
            {
                // Read current desired state, not a potentially stale queue payload.
                var domain = await db.Domains.AsNoTracking().SingleOrDefaultAsync(x => x.Id == group.Key, ct);
                var name = "site-" + group.Key.ToString("N");
                if (domain is null)
                    await KubectlAsync(null, ct, "delete", "httproute,deployment,service,configmap", name,
                        "--ignore-not-found=true");
                else
                    await KubectlAsync(BuildManifest(domain), ct, "apply", "--server-side",
                        "--field-manager=hostingpanel", "-f", "-");
                foreach (var row in group)
                {
                    row.Status = "completed";
                    row.CompletedAt = DateTimeOffset.UtcNow;
                    row.LastError = null;
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                latest.Attempts++;
                latest.LastError = "Website reconciliation failed; see application log.";
                logger.LogWarning(ex, "Could not reconcile website {Id}", group.Key);
            }
            await db.SaveChangesAsync(ct);
        }
    }

    private string Required(string key) => configuration["KubernetesHosting:" + key] is { Length: > 0 } value
        ? value : throw new InvalidOperationException("Missing KubernetesHosting:" + key);

    private string Namespace => configuration["KubernetesHosting:Namespace"] ?? "hosting";

    private string BuildManifest(HostedDomain domain)
    {
        var host = domain.Name;
        if (host.Length > 253 || host.Split('.').Any(label => label.Length is < 1 or > 63
            || !char.IsAsciiLetterOrDigit(label[0]) || !char.IsAsciiLetterOrDigit(label[^1])
            || label.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')))
            throw new InvalidOperationException("Invalid hostname");
        using var doc = JsonDocument.Parse(domain.SettingsJson);
        bool Flag(string key, bool fallback) => doc.RootElement.TryGetProperty(key, out var value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : fallback;
        var php = Flag("php", true);
        var tls = Flag("ssl", true);
        if (Flag("cgi", false)) throw new InvalidOperationException("CGI is not supported");
        var name = "site-" + domain.Id.ToString("N");
        var labels = new Dictionary<string,string> { ["app.kubernetes.io/name"] = name };
        var root = "/var/www/sites/" + domain.Id.ToString("N");
        var phpVersion = configuration["KubernetesHosting:DefaultPhpVersion"] ?? "8";
        if (phpVersion is not ("7" or "8")) throw new InvalidOperationException("Unsupported PHP version");
        if (php && phpVersion == "7" && !configuration.GetValue<bool>("KubernetesHosting:EnablePhp7"))
            throw new InvalidOperationException("PHP 7 is disabled");
        var apache = """
            ServerRoot "/usr/local/apache2"
            Listen 8080
            ServerName localhost
            LoadModule mpm_event_module modules/mod_mpm_event.so
            LoadModule authz_core_module modules/mod_authz_core.so
            LoadModule unixd_module modules/mod_unixd.so
            LoadModule log_config_module modules/mod_log_config.so
            LoadModule dir_module modules/mod_dir.so
            LoadModule mime_module modules/mod_mime.so
                        LoadModule proxy_module modules/mod_proxy.so
            LoadModule proxy_fcgi_module modules/mod_proxy_fcgi.so
            LoadModule rewrite_module modules/mod_rewrite.so
            User daemon
            Group daemon
            ErrorLog /proc/self/fd/2
            CustomLog /proc/self/fd/1 common
            TypesConfig conf/mime.types
            """ + "\n<VirtualHost *:8080>\nServerName " + host + "\nDocumentRoot " + root +
            "\n<Directory " + root + ">\nOptions -Indexes -ExecCGI -Includes\nAllowOverride None\nRequire all granted\nDirectoryIndex index.php index.html\n</Directory>\n" +
            (php ? "<FilesMatch \"\\.php$\">\nSetHandler \"proxy:fcgi://php-fpm-" + phpVersion + ":9000\"\n</FilesMatch>\n"
            : "<FilesMatch \"\\.php$\">\nRequire all denied\n</FilesMatch>\n") + "</VirtualHost>\n";
        var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apache)));
        object Meta() => new { name, @namespace = Namespace, labels };
        object DataMount(bool readOnly) => new { name = "data", mountPath = root, readOnly };
        var containers = new object[] {
            new { name = "apache", image = configuration["KubernetesHosting:ApacheImage"] ?? "httpd:2.4",
                command = new[] { "httpd-foreground" }, args = new[] { "-f", "/etc/site/httpd.conf" },
                ports = new[] { new { containerPort = 8080 } },
                readinessProbe = new { tcpSocket = new { port = 8080 } },
                volumeMounts = new object[] { DataMount(true), new { name = "config", mountPath = "/etc/site", readOnly = true } } }
        };
        var export = Required("NfsPath").TrimEnd('/');
        if (!export.StartsWith('/')) throw new InvalidOperationException("NFS path must be absolute");
        var resources = new object[] {
            new { apiVersion = "v1", kind = "ConfigMap", metadata = Meta(), data = new Dictionary<string,string> { ["httpd.conf"] = apache } },
            new { apiVersion = "apps/v1", kind = "Deployment", metadata = Meta(), spec = new {
                replicas = 1, selector = new { matchLabels = labels }, template = new {
                    metadata = new { labels, annotations = new Dictionary<string,string> { ["hostingpanel/config-sha256"] = checksum } },
                    spec = new { automountServiceAccountToken = false, containers, volumes = new object[] {
                        new { name = "config", configMap = new { name } },
                        new { name = "data", nfs = new { server = Required("NfsServer"), path = export + "/" + domain.Id.ToString("N") } }
                    } }
                }
            } },
            new { apiVersion = "v1", kind = "Service", metadata = Meta(), spec = new { selector = labels, ports = new[] { new { port = 80, targetPort = 8080 } } } },
            new { apiVersion = "gateway.networking.k8s.io/v1", kind = "HTTPRoute", metadata = Meta(), spec = new {
                parentRefs = new[] { new { name = Required("GatewayName"), @namespace = Required("GatewayNamespace"),
                    sectionName = Required(tls ? "HttpsListener" : "HttpListener") } },
                hostnames = new[] { host }, rules = new[] { new { backendRefs = new[] { new { name, port = 80 } } } }
            } }
        };
        return JsonSerializer.Serialize(new { apiVersion = "v1", kind = "List", items = resources });
    }

    private string BuildRuntimeManifest()
    {
        if (!configuration.GetValue<bool>("KubernetesHosting:AllowSharedPhpRuntime"))
            throw new InvalidOperationException("Shared PHP requires explicit trust in all hosted PHP applications.");
        var resources = new List<object>();
        foreach (var version in new[] { "8", "7" })
        {
            if (version == "7" && !configuration.GetValue<bool>("KubernetesHosting:EnablePhp7")) continue;
            var name = "php-fpm-" + version;
            var labels = new Dictionary<string,string> { ["app.kubernetes.io/name"] = name };
            object Meta() => new { name, @namespace = Namespace, labels };
            var image = version == "7" ? Required("Php7Image") : configuration["KubernetesHosting:Php8Image"] ?? "php:8.4-fpm";
            resources.Add(new { apiVersion = "apps/v1", kind = "Deployment", metadata = Meta(), spec = new {
                replicas = 1, selector = new { matchLabels = labels }, template = new {
                    metadata = new { labels }, spec = new {
                        automountServiceAccountToken = false,
                        containers = new[] { new { name = "php-fpm", image,
                            ports = new[] { new { containerPort = 9000 } },
                            readinessProbe = new { tcpSocket = new { port = 9000 } },
                            volumeMounts = new[] { new { name = "sites", mountPath = "/var/www/sites" } } } },
                        volumes = new[] { new { name = "sites", nfs = new { server = Required("NfsServer"), path = Required("NfsPath") } } }
                    }
                }
            } });
            resources.Add(new { apiVersion = "v1", kind = "Service", metadata = Meta(), spec = new {
                type = "ClusterIP", selector = labels, ports = new[] { new { port = 9000, targetPort = 9000 } }
            } });
        }
        return JsonSerializer.Serialize(new { apiVersion = "v1", kind = "List", items = resources });
    }

    private async Task KubectlAsync(string? manifest, CancellationToken ct, params string[] args)
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
            await stdout;
            var error = await stderr;
            if (process.ExitCode != 0) throw new InvalidOperationException("kubectl failed: " + error);
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }
}
