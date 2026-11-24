using System.Diagnostics;
using System.Text.Json;
namespace HostingPanel.Providers.CertManager;

public sealed class CertManagerClient(string kubectl, string ns)
{
    public static string Name(Guid id) => "site-" + id.ToString("N");
    public async Task<string> ExecuteAsync(string? input, CancellationToken ct, params string[] arguments)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        var start = new ProcessStartInfo(kubectl) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-n"); start.ArgumentList.Add(ns);
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start kubectl.");
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(token);
            var error = process.StandardError.ReadToEndAsync(token);
            if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(token);
            await error;
            if (process.ExitCode != 0) throw new InvalidOperationException("cert-manager Kubernetes operation failed.");
            return await output;
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }
    public Task<string> ApplyAsync(Guid id, string[] dnsNames, string issuer, string kind, string keyType, CancellationToken ct)
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
        return ExecuteAsync(manifest, ct, "apply", "--server-side", "--field-manager=hostingpanel-certificate", "-f", "-");
    }
    public Task<string> ListAsync(CancellationToken ct) => ExecuteAsync(null, ct, "get", "certificates.cert-manager.io", "-l", "hostingpanel/component=certificate", "-o", "json");
    public Task<string> DeleteAsync(string name, CancellationToken ct) => ExecuteAsync(null, ct, "delete", "certificate.cert-manager.io", name, "--ignore-not-found=true");
}
