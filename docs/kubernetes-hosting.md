# Kubernetes hosting

Enable the Kubernetes backend and KubernetesHosting:Enabled. The application uses KubernetesResourceClient (Kubernetes .NET SDK) with in-cluster credentials or local kubeconfig. Do not install or invoke kubectl in application runtime. Operator commands may use kubectl.

## Shared runtime
WebsiteReconciler generates immutable Apache configuration snapshots on dedicated shared NFS and HTTPRoutes pointing to apache-baseline. Apache runs as a shared StatefulSet with stable pod names to scope reload authorization to named pods. The controller validates snapshots using httpd, atomically selects active.conf and requests graceful reload through the Kubernetes SDK; rollback restores the previous revision on failure. No vhost-sync sidecar or per-domain ConfigMap publication. Legacy per-domain ConfigMaps are retired after successful NFS activation using label, name, data and object-version checks; failed cleanup is retried. A dedicated existing NFS export (apacheConfigPath on nfsServer) is mandatory, separate from customer storage. Both workloads mount the export directly; only the panel can write, Apache mounts it read-only, and PHP receives no configuration mount. No Apache PVC or StorageClass is needed. Runtime acceptance remains pending.

PHP-FPM uses a dedicated Deployment, ClusterIP Service and ingress NetworkPolicy per account. Administrators configure trusted images in BackendSettings.PhpImages (first entry is the default); admins and authorized resellers select an image and CPU/memory requests and limits, worker count and idle timeout from the account detail page. Account overrides take precedence over package defaults; reset restores inheritance. One replica with Recreate strategy avoids overlapping resource budgets but causes brief downtime during changes. DirectAdmin does not support these Kubernetes-specific settings. Legacy per-domain PHP version overrides are no longer used. PHP runs as the detected account UID/GID with only that account's NFS directory mounted, on-demand workers and resource limits. NFS must permit mounting account subdirectories and the CNI must enforce NetworkPolicy. Legacy shared workloads are retained for operator-controlled migration; remove them after validating new endpoints and Apache convergence. Runtime acceptance remains pending.

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


### Mail management API (initial integration)

Enable `stalwart.api.enabled`, set `stalwart.api.baseUrl` to an HTTPS origin reachable by the panel with a trusted matching certificate, and set `stalwart.api.existingSecret` to a Secret with key `api-key`. This is independent of the bundled workload, so an external dedicated Stalwart v1 instance is supported. Never reuse the bootstrap recovery identity. Configure a restricted management identity with domain query/get/create and account query/get/create/update/destroy permissions; Stalwart also requires sufficient permissions for managed users. Do not mix infrastructure administrator accounts with customer mail domains. TLS verification remains enabled, and redirects are disabled.

All endpoints require panel Basic authentication and a visible, non-suspended hosted domain. The first mailbox creation creates the Stalwart domain if absent. Existing matching mail domains are treated as managed; use a dedicated instance and verify ownership before enabling. No local mailbox mirror is maintained.

The official legacy DirectAdmin endpoint is `/CMD_API_POP`. The pinned modern DirectAdmin Swagger and its routes remain unchanged.

| Method | Action | Form / query fields |
| --- | --- | --- |
| GET | list | domain |
| POST | create | domain, user, passwd, passwd2, quota (MiB, positive integer) |
| POST | modify | domain, user, quota (bytes, positive integer) |
| POST | delete | domain, user |

Responses are URL-encoded legacy fields. Unsupported actions (including full_list, detailed quota, multiple deletion, unlimited quotas, send limits and rename) fail explicitly. State-changing GET requests are rejected.

Password reset, aliases, forwarding, aggregate hosting package limits, mail DNS, suspension propagation and domain deletion cleanup are pending. Mutations are administrator-only until aggregate resource limits and lifecycle integration have been completed; resellers and users can only list accounts for their visible domains. An upstream failure may leave a newly created mail domain; no automatic destructive rollback is attempted. DirectAdmin forwards CMD_API_POP to its upstream with the caller's credentials.

The additional provider-neutral API uses the same IMailProvider and authorization, with JSON responses and quotaBytes expressed in bytes for both create and update. Its separate OpenAPI contract is available at /openapi/hosting-mail.json and in the Swagger UI selector.

| Method | Path |
| --- | --- |
| GET, POST | /hosting/mail/{domain}/accounts |
| PATCH, DELETE | /hosting/mail/{domain}/accounts/{name} |

DELETE requires confirmation=name@domain. The additional API uses DirectAdminMailProvider on the DirectAdmin backend; CMD_API_POP still forwards unchanged to DirectAdmin. No new modern DirectAdmin /api operations are claimed.

Mailbox creation now requires an explicit `nemails` entitlement in the existing hosting package or account override (nonnegative integer, or explicit package `unlimited`). All current domains owned by the account are counted, including suspended domains. Missing limits deny creation. A process-wide gate serializes panel mail mutations; only a single panel replica with no external mailbox writers is supported for this limit. Deleted/orphaned domains, aggregate disk quota and lifecycle synchronization still require separate implementation. Administrators remain the only permitted Stalwart mutators.

### Automatic mail DNS provisioning

With PowerDNS and Stalwart management enabled, `stalwart.api.autoProvisionDns` defaults to true. A mailbox/forwarder creation request schedules durable `mail-dns` reconciliation before creating the upstream domain. The worker imports `x:Domain/get` `dnsZoneFile` MX/SPF/DKIM/DMARC records into the existing owned HostingPanel DNS zone, then queues normal PowerDNS reconciliation. Stalwart must have its public mail hostname, signing keys and mail configuration set up. Do not configure another writer for the same zone. Conflicts block import and preserve customer records. A missing zone waits for a DNS invalidation; an upstream record file that is not ready remains retryable. The mail-dns operation records staging success, not public DNS propagation; dns-zone operations track PowerDNS apply. Existing mail domains are imported on another mailbox/forwarder creation. Optional periodic refresh of panel-owned mail records runs in a separate scheduler, is disabled by default; when enabled its interval defaults to 60 minutes (configurable via `stalwart.api.dnsSyncMinutes`, clamped to 5–1440), and can be disabled via `stalwart.api.periodicDnsSync: false`. Native DKIM rotation supports automatic PowerDNS provider registration (`stalwart.api.autoRegisterDnsProvider`, default true) or an explicit `stalwart.api.dkimDnsServerId`. The panel imports MX, SPF and DMARC once and enables Automatic DNS management for DKIM only, plus Automatic DKIM management if necessary. Existing unrelated automatic DNS settings are rejected. PowerDNS preserves apex MX and TXT values during ordinary zone reconciliation. TXT is freely editable, including SPF/DKIM/DMARC; explicit durable commands modify live RRsets and retain unrelated values. MX is freely editable, including for external mail providers such as Microsoft 365 and Google Workspace. Concurrent Stalwart writes may race with user changes; users are responsible for mail configuration they change. Native mode disables scheduled imports. Changes to external Stalwart configuration may require an explicit Stalwart DnsManagement task. Keep delegation configured once enabled. Stalwart defaults are 90 days rotation, 7 days retirement and 30 days deletion unless already customized. Reverse DNS and external mail-host address records are not managed. Periodic import still refuses conflicting non-DKIM records rather than overwriting them.
