# Kubernetes hosting

Enable the Kubernetes backend and KubernetesHosting:Enabled. The application uses KubernetesResourceClient (Kubernetes .NET SDK) with in-cluster credentials or local kubeconfig. Do not install or invoke kubectl in application runtime. Operator commands may use kubectl.

## Shared runtime
WebsiteReconciler generates immutable Apache configuration snapshots on dedicated shared NFS and HTTPRoutes pointing to apache-baseline. Apache runs as a shared StatefulSet with stable pod names to scope reload authorization to named pods. The controller validates snapshots using httpd, atomically selects active.conf and requests graceful reload through the Kubernetes SDK; rollback restores the previous revision on failure. No vhost-sync sidecar or per-domain ConfigMap publication. Legacy per-domain ConfigMaps are retired after successful NFS activation using label, name, data and object-version checks; failed cleanup is retried. A dedicated existing NFS export (apacheConfigPath on nfsServer) is mandatory, separate from customer storage. Both workloads mount the export directly; only the panel can write, Apache mounts it read-only, and PHP receives no configuration mount. No Apache PVC or StorageClass is needed. Runtime acceptance remains pending.

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

## Database infrastructure

Optional MariaDB uses its own ReadWriteOnce filesystem PVC, independent of the NFS website export. See [installation: MariaDB and iSCSI alongside NFS](installation.md#mariadb-and-iscsi-alongside-nfs). The Kubernetes backend provisions databases and users through MariaDB when configured. Database-level grants explicitly match the database privileges delegated to the Helm bootstrap account; SQL failures are logged without passwords or raw SQL.

## Mail infrastructure

Set `stalwart.enabled: true` with the Kubernetes backend to deploy the official Stalwart v1 image. The single StatefulSet uses separate ReadWriteOnce filesystem claims for /etc/stalwart and /var/lib/stalwart. Set storageClassName for CSI/iSCSI, or supply existingConfigClaim and existingDataClaim. StatefulSet-generated claims are retained by default when the workload is removed; back up both volumes. Do not scale the embedded RocksDB deployment to multiple replicas.

For initial setup, create a Secret with key recovery-admin containing a strong username:password pair and reference it via stalwart.existingSecret. Alternatively retrieve the temporary password from restricted pod logs. Use a private kubectl port-forward:

```sh
kubectl -n hosting port-forward statefulset/admin-stalwart 8080:8080
```

Open http://127.0.0.1:8080/admin, complete the setup wizard with the real mail hostname, domain, persistent data directory and console logging, then remove the recovery Secret reference and restart the StatefulSet. The normal HTTPS management listener is on port 443; use the internal service or an authenticated private management path. Restrict access to the bootstrap listener and pod network using cluster network controls; ClusterIP alone is not tenant isolation.

The external service exposes TCP 25, 465, 993 and 4190. Optional 587 and 995 require matching Stalwart listeners. HTTPS is disabled externally by default because it also serves administration; enabling it for ACME TLS-ALPN-01 or JMAP requires reviewing administration access controls. Otherwise configure trusted certificates manually or use DNS-based ACME. Configure public mail IP, PTR, inbound and outbound TCP 25, MX, SPF, DKIM and DMARC before accepting customer traffic. Verify source-IP preservation and outbound SMTP IP with the actual load balancer and network. No HTTPRoute is required for mail TCP ports. The chart does not configure listeners, DNS or mail accounts automatically. See [official Stalwart installation](https://www.stalwart.email/docs/install/platform/docker/).

