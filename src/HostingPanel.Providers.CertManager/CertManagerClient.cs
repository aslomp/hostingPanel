using System.Text.Json;
namespace HostingPanel.Providers.CertManager;

public sealed class CertManagerClient(KubernetesResourceClient resources, string ns)
{
    public static string Name(Guid id) => "site-" + id.ToString("N");
    public Task ApplyAsync(Guid id, string[] dnsNames, string issuer, string kind, string keyType, CancellationToken ct) =>
        resources.ApplyAsync(BuildManifest(id, dnsNames, issuer, kind, keyType), ns, "hostingpanel-certificate", ct);

    public string BuildManifest(Guid id, string[] dnsNames, string issuer, string kind, string keyType)
    {
        var name = Name(id);
        var algorithm = keyType.StartsWith("ec", StringComparison.Ordinal) ? "ECDSA" : "RSA";
        var size = int.Parse(keyType[(algorithm == "ECDSA" ? 2 : 3)..], System.Globalization.CultureInfo.InvariantCulture);
        var manifest = JsonSerializer.Serialize(new {
            apiVersion = "cert-manager.io/v1", kind = "Certificate",
            metadata = new { name, @namespace = ns, labels = new Dictionary<string,string> { ["hostingpanel/component"] = "certificate" } },
            spec = new { secretName = name + "-tls", dnsNames, issuerRef = new { name = issuer, kind, group = "cert-manager.io" },
                privateKey = new { algorithm, size, rotationPolicy = "Always" } }
        });
        return manifest;
    }
    public Task<string> ListAsync(CancellationToken ct) => resources.ListAsync("cert-manager.io", "v1", ns, "certificates", "hostingpanel/component=certificate", ct);
    public Task DeleteAsync(string name, CancellationToken ct) => resources.DeleteAsync("cert-manager.io", "v1", ns, "certificates", name, ct);
    public Task<string> ListRequestsAsync(CancellationToken ct) => resources.ListAsync("cert-manager.io", "v1", ns, "certificaterequests", null, ct);
    public Task<string> ListOrdersAsync(CancellationToken ct) => resources.ListAsync("acme.cert-manager.io", "v1", ns, "orders", null, ct);
    public Task<string> ListChallengesAsync(CancellationToken ct) => resources.ListAsync("acme.cert-manager.io", "v1", ns, "challenges", null, ct);
}
