using System.Text.Json;
using HostingPanel.Api;

internal static class CertificateReconciliationTests
{
    public static void Run()
    {
        var work = new CertificateWork();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        Require(work.Take() is null);
        work.DomainChanged(a);
        work.DomainChanged(a);
        work.DomainChanged(b);
        Require(work.Take()!.ToHashSet().SetEquals([a, b]));
        work.DomainChanged(a);
        work.AllChanged();
        Require(work.Take() is null);
        Require(work.Take()!.Length == 0);
        work.Recover();
        Require(work.Take() is null);
        Require(CertificateReconciler.TryDomainId("site-" + a.ToString("N"), out var parsed) && parsed == a);
        Require(!CertificateReconciler.TryDomainId("unmanaged", out _));
        using var desired = JsonDocument.Parse("""{"dnsNames":["example.com"],"issuerRef":{"name":"issuer"}}""");
        using var observed = JsonDocument.Parse("""{"dnsNames":["example.com"],"issuerRef":{"name":"issuer","group":"cert-manager.io"},"duration":"2160h"}""");
        Require(CertificateReconciler.MatchesDesired(desired.RootElement, observed.RootElement));
        using var changed = JsonDocument.Parse("""{"dnsNames":["other.example.com"],"issuerRef":{"name":"issuer"}}""");
        Require(!CertificateReconciler.MatchesDesired(changed.RootElement, observed.RootElement));
        using var stale = JsonDocument.Parse("""{"metadata":{"generation":2},"status":{"conditions":[{"type":"Ready","status":"True","observedGeneration":1}]}}""");
        using var ready = JsonDocument.Parse("""{"metadata":{"generation":2},"status":{"conditions":[{"type":"Ready","status":"True","observedGeneration":2}]}}""");
        Require(!CertificateReconciler.IsReady(stale.RootElement));
        Require(CertificateReconciler.IsReady(ready.RootElement));
    }

    private static void Require(bool condition)
    {
        if (!condition) throw new Exception("Certificate reconciliation contract failed.");
    }
}
