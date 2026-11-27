using System.Text.Json;
using HostingPanel.Api;
using HostingPanel.Providers.CertManager;

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
        Require(CertificateReconciler.TryTlsDomainId("hp-tls-" + a.ToString("N"), out parsed) && parsed == a);
        Require(!CertificateReconciler.TryTlsDomainId("site-" + a.ToString("N"), out _));
        using var certA = JsonDocument.Parse("""{"metadata":{"uid":"a","generation":1,"resourceVersion":"1"},"spec":{"secretName":"tls"},"status":{"conditions":[{"type":"Ready","status":"True","observedGeneration":1}],"renewalTime":"old"}}""");
        using var certB = JsonDocument.Parse("""{"metadata":{"uid":"a","generation":1,"resourceVersion":"2"},"spec":{"secretName":"tls"},"status":{"conditions":[{"type":"Ready","status":"True","observedGeneration":1}],"renewalTime":"new"}}""");
        using var certC = JsonDocument.Parse("""{"metadata":{"uid":"a","generation":1},"spec":{"secretName":"tls"},"status":{"conditions":[{"type":"Ready","status":"False"}]}}""");
        Require(KubernetesResourceClient.CertificateFingerprint(certA.RootElement) == KubernetesResourceClient.CertificateFingerprint(certB.RootElement));
        Require(KubernetesResourceClient.CertificateFingerprint(certA.RootElement) != KubernetesResourceClient.CertificateFingerprint(certC.RootElement));
        using var routeA = JsonDocument.Parse("""{"metadata":{"uid":"r","resourceVersion":"1"},"spec":{"hostnames":["a.test"]},"status":{"parents":[]}}""");
        using var routeB = JsonDocument.Parse("""{"metadata":{"uid":"r","resourceVersion":"2"},"spec":{"hostnames":["a.test"]},"status":{"parents":[{"controllerName":"envoy"}]}}""");
        using var routeC = JsonDocument.Parse("""{"metadata":{"uid":"r"},"spec":{"hostnames":["b.test"]}}""");
        Require(KubernetesResourceClient.RoutingFingerprint(routeA.RootElement) == KubernetesResourceClient.RoutingFingerprint(routeB.RootElement));
        Require(KubernetesResourceClient.RoutingFingerprint(routeA.RootElement) != KubernetesResourceClient.RoutingFingerprint(routeC.RootElement));
    }

    private static void Require(bool condition)
    {
        if (!condition) throw new Exception("Certificate reconciliation contract failed.");
    }
}
