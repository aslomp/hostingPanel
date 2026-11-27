using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HostingPanel.Providers.CertManager;

namespace HostingPanel.Api;

internal sealed class AccountPhpRuntime(IConfiguration configuration, KubernetesResourceClient resources, string ns)
{
    internal static string Name(Guid accountId, string version) => "hp-fpm-" + accountId.ToString("N") + "-" + version;

    private string Required(string key) => configuration["KubernetesHosting:" + key] is { Length: > 0 } value
        ? value : throw new InvalidOperationException("Missing KubernetesHosting:" + key);

    internal async Task<HashSet<string>> EnsureAsync(
        IEnumerable<(string Username, PhpAccountIdentity Identity, string Version)> sites, CancellationToken ct)
    {
        var desired = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (username, identity, version) in sites.DistinctBy(x => (x.Identity.AccountId, x.Version)))
        {
            var name = Name(identity.AccountId, version);
            await resources.ApplyAsync(BuildManifest(username, identity, version), ns, "hostingpanel", ct);
            desired.Add(name);
        }
        return desired;
    }

    internal string BuildManifest(string username, PhpAccountIdentity identity, string version)
    {
        if (version is not ("7" or "8") || (version == "7" && !configuration.GetValue<bool>("KubernetesHosting:EnablePhp7")))
            throw new InvalidOperationException("Unsupported or disabled PHP version.");
        if (string.IsNullOrEmpty(username) || username.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_') ||
            identity.Uid <= 0 || identity.Gid <= 0)
            throw new InvalidOperationException("Invalid PHP account identity.");
        var name = Name(identity.AccountId, version);
        var labels = new Dictionary<string, string> {
            ["app.kubernetes.io/name"] = name, ["hostingpanel/component"] = "account-php"
        };
        object Meta() => new { name, @namespace = ns, labels };
        const string pool = "[global]\nerror_log = /proc/self/fd/2\ndaemonize = no\n[account]\nlisten = 0.0.0.0:9000\npm = ondemand\npm.max_children = 8\npm.process_idle_timeout = 10s\nclear_env = yes\nsecurity.limit_extensions = .php\n";
        var data = new Dictionary<string, string> { ["php-fpm.conf"] = pool };
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pool)));
        var image = version == "7" ? Required("Php7Image") : configuration["KubernetesHosting:Php8Image"] ?? "php:8.6.0-fpm";
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
                replicas = configuration.GetValue<int?>("KubernetesHosting:Php" + version + "Replicas") ?? 1,
                selector = new { matchLabels = labels }, template = new {
                    metadata = new { labels, annotations = new Dictionary<string,string> { ["hostingpanel/config-sha256"] = hash } },
                    spec = new {
                        automountServiceAccountToken = false,
                        containers = new[] { new { name = "php-fpm", image,
                            command = new[] { "php-fpm", "-F", "-y", "/etc/hostingpanel/php-fpm.conf" },
                            securityContext = new { runAsUser = identity.Uid, runAsGroup = identity.Gid, runAsNonRoot = true,
                                allowPrivilegeEscalation = false, readOnlyRootFilesystem = true,
                                capabilities = new { drop = new[] { "ALL" } }, seccompProfile = new { type = "RuntimeDefault" } },
                            resources = new { requests = new { cpu = "25m", memory = "32Mi" }, limits = new { cpu = "1", memory = "256Mi" } },
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
