# Kubernetes hosting

Enable the Kubernetes backend and KubernetesHosting:Enabled. The application uses KubernetesResourceClient (Kubernetes .NET SDK) with in-cluster credentials or local kubeconfig. Do not install or invoke kubectl in application runtime. Operator commands may use kubectl.

## Shared runtime
WebsiteReconciler generates a ConfigMap per domain containing vhost.conf and a Gateway API HTTPRoute pointing to apache-baseline. The chart provides a shared Apache workload/service; ordinary domain changes must not create one workload per customer. Existing legacy deployments are retired explicitly after validation. The chart uses a vhost-sync sidecar and shell-based httpd configuration validation/graceful reload; the planned replacement is backend-owned shared NFS configuration, without a separate agent. Candidate revision metadata storage is preparatory; executable configuration publication and activation are not implemented. The vhost synchronization mechanism and chart are authoritative for reload behavior. The production hardening plan is separate.

PHP-FPM uses shared runtimes and account pools where enabled. AllowSharedPhpRuntime is explicit opt-in for trusted applications, not hard tenant isolation. Validate NFS permissions, CNI enforcement, account identities and FastCGI access; do not expose FastCGI publicly. Inspect current chart and reconciler for version and replica settings.

## Storage and lifecycle
Preserve the DirectAdmin /home/<username>/domains/<domain>/public_html layout. NfsServer/NfsPath identify the existing export; NfsProvisioningPath or provisioningClaim must present the same filesystem with appropriate authorized ownership/creation rights. Avoid recursive chmod/chown, path migration or weakening root squash. The PHP identity extension allocates stable per-account identity/port; existing filesystem identity is relevant. Domain cleanup removes owned files after dependent operations; it is not a quarantine workflow. Verify deletion and permissions on isolated fixtures before production.

## Routing, certificates and DNS
Install Gateway API/Envoy and configure the existing gateway and listeners. Install cert-manager separately if enabled; configure issuer mappings and an existing authorized ListenerSet when used. Enable PowerDNS and configure valid IP and nameserver defaults for automatic zones. See [DNS](dns.md), [PowerDNS](powerdns.md) and [certificates](certificates.md).

## Deployment
Use deploy/helm/values-hosting.example.yaml and the chart's values.yaml as the current schema. Configure image, NFS, Gateway, namespace, bootstrap secret, DNS and certificate settings as appropriate. Use one panel replica per SQLite database and a persistent volume with reliable locking. Render manifests before deploying.

```sh
helm template panel deploy/helm/hostingpanel -n hosting -f hosting-values.yaml
helm upgrade --install panel deploy/helm/hostingpanel -n hosting --create-namespace -f hosting-values.yaml
kubectl -n hosting get deployments,services,pods
```

The code contains website, DNS and certificate reconcilers, but production isolation, reload behavior, load capacity, cleanup and observed readiness require validation. See [production plan](plans/apache-production.md).
