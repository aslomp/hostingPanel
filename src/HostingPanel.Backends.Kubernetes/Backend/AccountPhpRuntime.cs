using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HostingPanel.Providers.CertManager;

namespace HostingPanel.Api;

internal sealed class AccountPhpRuntime(IConfiguration configuration, KubernetesResourceClient resources, string ns)
{
    internal static string Name(Guid accountId) => "hp-fpm-" + accountId.ToString("N");

    private string Required(string key) => configuration["KubernetesHosting:" + key] is { Length: > 0 } value
        ? value : throw new InvalidOperationException("Missing KubernetesHosting:" + key);

    internal async Task<HashSet<string>> EnsureAsync(
        IEnumerable<(string Username, PhpAccountIdentity Identity, PhpRuntimeSettings Settings)> sites, CancellationToken ct)
    {
        var desired = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (username, identity, settings) in sites.DistinctBy(x => x.Identity.AccountId))
        {
            var name = Name(identity.AccountId);
            await resources.ApplyAsync(BuildManifest(username, identity, settings), ns, "hostingpanel", ct);
            desired.Add(name);
        }
        return desired;
    }

    internal string BuildManifest(string username, PhpAccountIdentity identity, PhpRuntimeSettings settings)
    {
        if (string.IsNullOrEmpty(username) || username.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_') ||
            identity.Uid <= 0 || identity.Gid <= 0)
            throw new InvalidOperationException("Invalid PHP account identity.");
        var name = Name(identity.AccountId);
        var labels = new Dictionary<string, string> {
            ["app.kubernetes.io/name"] = name, ["hostingpanel/component"] = "account-php"
        };
        object Meta() => new { name, @namespace = ns, labels };
        var pool = $"[global]\nerror_log = /proc/self/fd/2\ndaemonize = no\n[account]\nlisten = 0.0.0.0:9000\npm = ondemand\npm.max_children = {settings.MaxWorkers}\npm.process_idle_timeout = {settings.IdleSeconds}s\nclear_env = yes\nsecurity.limit_extensions = .php\n";
        var data = new Dictionary<string, string> { ["php-fpm.conf"] = pool };
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pool)));
        var image = settings.Image;
        var items = new object[] {
            new { apiVersion = "networking.k8s.io/v1", kind = "NetworkPolicy", metadata = Meta(), spec = new {
                podSelector = new { matchLabels = labels }, policyTypes = new[] { "Ingress" },
                ingress = new[] { new { from = new[] { new { podSelector = new { matchLabels = new Dictionary<string,string> {
                    ["app.kubernetes.io/name"] = "apache-baseline" } } } }, ports = new[] { new { protocol = "TCP", port = 9000 } } } }
            } },
            new { apiVersion = "v1", kind = "ConfigMap", metadata = Meta(), data },
            new { apiVersion = "v1", kind = "Service", metadata = Meta(), spec = new {
                type = "ClusterIP", selector = labels, ports = new[] { new { name = "fastcgi", port = 9000, targetPort = 9000 } }
            } },
            new { apiVersion = "apps/v1", kind = "Deployment", metadata = Meta(), spec = new {
                replicas = 1,
                strategy = new { type = "Recreate" },
                selector = new { matchLabels = labels }, template = new {
                    metadata = new { labels, annotations = new Dictionary<string,string> { ["hostingpanel/config-sha256"] = hash } },
                    spec = new {
                        automountServiceAccountToken = false,
                        containers = new[] { new { name = "php-fpm", image,
                            command = new[] { "php-fpm", "-F", "-y", "/etc/hostingpanel/php-fpm.conf" },
                            securityContext = new { runAsUser = identity.Uid, runAsGroup = identity.Gid, runAsNonRoot = true,
                                allowPrivilegeEscalation = false, readOnlyRootFilesystem = true,
                                capabilities = new { drop = new[] { "ALL" } }, seccompProfile = new { type = "RuntimeDefault" } },
                            resources = new { requests = new { cpu = settings.CpuRequest + "m", memory = settings.MemoryRequest + "Mi" }, limits = new { cpu = settings.CpuLimit + "m", memory = settings.MemoryLimit + "Mi" } },
                            readinessProbe = new { tcpSocket = new { port = 9000 } },
                            volumeMounts = new object[] { new { name = "sites", mountPath = "/home/" + username },
                                new { name = "config", mountPath = "/etc/hostingpanel", readOnly = true },
                                new { name = "tmp", mountPath = "/tmp" } } } },
                        volumes = new object[] { new { name = "sites", nfs = new { server = Required("NfsServer"), path = Required("NfsPath").TrimEnd('/') + "/" + username } },
                            new { name = "config", configMap = new { name } }, new { name = "tmp", emptyDir = new { sizeLimit = "128Mi" } } }
                    }
                }
            } }
        };
        return JsonSerializer.Serialize(new { apiVersion = "v1", kind = "List", items });
    }
}
