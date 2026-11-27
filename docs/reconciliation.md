# Kubernetes reconciliation and resource lifecycle

The Kubernetes backend stages desired state in SQLite. KubernetesOperationQueue.Schedule adds an outbox record with resource type, resource ID, action and JSON payload; the caller commits it with resource state. ReconciliationOperations stores pending/completed state, attempts, error and timestamps. Not every resource type has its own external runtime reconciler: do not infer execution from a queued account/domain record.

## Website and DNS

WebsiteReconciler resolves current domain/account state, stable PHP identities and storage. It groups pending website operations by resource and considers current state rather than blindly replaying the payload. Storage errors are tracked per site. It applies per-domain routes or removes them for deleted domains, then publishes and activates a complete Apache NFS revision before completing website operations. Account PHP runtime manifests are applied before Apache activation. Obsolete account deployments, services and config maps are retired after activation; policies are retained pending safe pod termination checks. Initial reconciliation and ListenerSet changes can target existing domains. Legacy per-site deployments are not automatically retired.

DnsReconciler groups pending dns-zone work and reconciles current desired records by name; it also retries deferred domain-dns staging in independent scopes/transactions. Website and DNS publication are separate work streams. See [DNS](dns.md).

## Domain deletion

KubernetesDomainService validates exact confirmation and ownership, removes domain desired state and owned DNS/ACME metadata and schedules website/DNS/cleanup work. DeletingDomain:<name> is a durable deletion marker, not a quarantine or secondary resource store. Recreating the same name is blocked while cleanup is pending.

WebsiteReconciler.CleanupDeletedDomainsAsync waits for pending dependent website/DNS operations, confirms that the domain no longer exists and requires the deletion marker. If cert-manager is enabled it waits for certificate removal and removes the managed TLS route. It calls NfsDirectoryProvisioner.DeleteAsync and only then clears the marker and completes cleanup. Failures remain retryable. Do not claim TLS Secrets are unconditionally deleted; that relies on cert-manager owner references. Verify actual ListenerSet convergence and cleanup separately.

NfsDirectoryProvisioner preserves the existing DirectAdmin directory layout and validates ownership/path safety; it is the storage boundary, not a general file manager. PhpIdentityModel allocates stable account numbers from 10000–30000 using its versioned provider extension; existing filesystem UID/GID are detected at runtime and are not persisted as allocation fields. Retained allocation numbers must not be casually reused.

## Observability

KubernetesDomainService derives provisioning labels from website and DNS operation state (including Pending, Retrying, Configuration applied and Unknown). The domain list/detail can refresh on HostingOperationNotifications; this is an invalidation signal only. Configuration applied does not assert Apache loaded the latest configuration, a public DNS answer or HTTPS readiness. Certificate observation is described separately in [certificates](certificates.md).

The Helm chart provides a shared Apache StatefulSet, dedicated direct configuration NFS mount (read-only in Apache, writable in the panel, no PVC), and named-pod exec authorization for the backend. The backend validates candidate snapshots, atomically selects active.conf and requests graceful reload; failed activation attempts restore the previous revision. No Apache sidecar or ConfigMap polling. Live replica convergence and graceful reload acceptance remain to be tested.

Website reconciliation wakes on coalesced internal operation notifications, and on Kubernetes pod watch events, with a five-second retry interval for pending failures and a fifteen-minute idle recovery interval. Watch reconnect starts with a fresh list/resourceVersion. Apache replica identity includes pod UID, container ID and restart count; a changed replica set invalidates the activation cache. The actual active include is syntax-checked before graceful reload. Proof of serving convergence and live watch recovery testing are still outstanding.

Certificate and website reconciliation share the wake/retry/watch reconnect loop. Certificate changes and committed domain/ACME mutations enqueue domain IDs; account package changes enqueue affected domains, while global settings changes invalidate the complete inventory. Watch relist and hourly recovery cover missed notifications and external drift. Certificate writes are skipped when the owned spec already matches. Live custom-resource watch recovery still requires cluster validation.

Website and DNS use independent bounded invalidation channels; committed mutations signal relevant workers, while UI-only notifications do not wake them. DNS retains a five-minute maintenance sweep and five-second pending-work retry. CertificateWork retains keyed domain invalidations on its own channel and hourly recovery. Signals are drained before reconciliation; SQLite remains authoritative for durable operations.
