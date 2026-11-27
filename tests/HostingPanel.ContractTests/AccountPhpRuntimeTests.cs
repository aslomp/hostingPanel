using System.Text.Json;
using HostingPanel.Api;
using HostingPanel.Providers.CertManager;
using Microsoft.Extensions.Configuration;

internal static class AccountPhpRuntimeTests
{
    internal static void Run()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["KubernetesHosting:NfsServer"] = "storage.invalid",
            ["KubernetesHosting:NfsPath"] = "/export/home"
        }).Build();
        using var client = new KubernetesResourceClient();
        var runtime = new AccountPhpRuntime(config, client, "hosting");
        var first = new PhpAccountIdentity { AccountId = Guid.NewGuid(), Number = 10001 };
        var second = new PhpAccountIdentity { AccountId = Guid.NewGuid(), Number = 10002 };
        using var doc = JsonDocument.Parse(runtime.BuildManifest("alice", first, "8"));
        var items = doc.RootElement.GetProperty("items").EnumerateArray().ToArray();
        var name = AccountPhpRuntime.Name(first.AccountId, "8");
        Check(name != AccountPhpRuntime.Name(second.AccountId, "8"));
        Check(name != AccountPhpRuntime.Name(first.AccountId, "7"));
        Check(items.Length == 4 && items.All(x => x.GetProperty("metadata").GetProperty("name").GetString() == name));
        var pod = items.Single(x => x.GetProperty("kind").GetString() == "Deployment")
            .GetProperty("spec").GetProperty("template").GetProperty("spec");
        Check(!pod.GetProperty("automountServiceAccountToken").GetBoolean());
        var container = pod.GetProperty("containers")[0];
        Check(container.GetProperty("securityContext").GetProperty("runAsUser").GetInt32() == 10001);
        Check(container.GetProperty("securityContext").GetProperty("runAsNonRoot").GetBoolean());
        Check(pod.GetProperty("volumes")[0].GetProperty("nfs").GetProperty("path").GetString() == "/export/home/alice");
        Check(container.GetProperty("volumeMounts")[0].GetProperty("mountPath").GetString() == "/home/alice");
        Check(items.Single(x => x.GetProperty("kind").GetString() == "NetworkPolicy").GetRawText().Contains("apache-baseline"));
        Check(items.Single(x => x.GetProperty("kind").GetString() == "ConfigMap").GetProperty("data")
            .GetProperty("php-fpm.conf").GetString()!.Contains("pm = ondemand"));
        try { runtime.BuildManifest("../bob", first, "8"); throw new Exception("Unsafe path accepted"); }
        catch (InvalidOperationException) { }
    }

    private static void Check(bool condition)
    {
        if (!condition) throw new Exception("Account PHP runtime invariant failed.");
    }
}
