# TLS and certificate lifecycle

## DirectAdmin

DirectAdminBackend implements ICertificateProvider by forwarding official upstream requests. TlsController forwards server-level TLS/ACME operations. DirectAdmin owns certificate issuance, renewal, configuration and status. Do not provision cert-manager resources for this backend.

## Kubernetes configuration and intent

KubernetesCertificateProvider handles the supported official session ACME and domain TLS route subset. Domain ACME intent is stored in Configuration under Acme:Domain:<domain GUID in N format>. Domain settings and certificate settings are separate; neither contains private keys.

Issuer resolution order: explicit domain Provider, AcmeUserProviders keyed by account username, AcmePackageProviders keyed by package GUID string, then AcmeDefaultProvider. AcmeProviders maps the ID to an existing Issuer or ClusterIssuer and includes staging/wildcard metadata. ResolveIssuer is the canonical resolver. Domain names are the apex plus optional wildcard, excluding SkipDNSNames. DNS solver credentials and EAB configuration belong on the external issuer; unsupported inline fields are rejected. Enabling ACME requires a valid mapped issuer and compatible name selection.

## Reconciliation and observed status

CertificateReconciler runs when both CertManager:Enabled and KubernetesHosting:Enabled. Startup, committed desired-state notifications and independent Certificate, configured ListenerSet and labelled HTTPS HTTPRoute LIST/WATCH streams trigger reconciliation. The shared channel loop coalesces invalidations and retries failed work after five seconds; there is no idle reconciliation timer. Reconnects use a fresh LIST/resourceVersion and compare relevant snapshots so unchanged relists do not reconcile. Certificate readiness/spec changes and routing spec/ownership/managed shim annotation changes enqueue work; ordinary status changes do not. It compares owned Certificate spec fields before applying, uses readiness only for matching specs (and matching observed generation when available), and removes obsolete managed certificates. Normal certificate and domain events coalesce by domain ID; only those domains and Certificates are read and their TLS routes reconciled. The shared ListenerSet is generated from the complete in-memory readiness inventory. Startup, changed watch relist, global settings changes and recovery rebuild the complete inventory. Failed work forces a recovery pass. cert-manager handles actual issuance and renewal. The manual provision route submits a Certificate request and does not prove issuance. The dry-run route summarizes intent/observed coverage; it is not a real ACME validation.

Certificate status is queried directly on authorized API reads, independently of reconciliation and without persisting status to SQLite. Certificate watch status changes invalidate UI reads. CertificateSnapshot reads Certificate, CertificateRequest, Order and Challenge resources through CertManagerClient; public certificate material and status are used to produce the certificate inventory and ACME log views. No TLS private-key/Secret read is needed for these observations. Do not interpret estimated/placeholder fields in the compatibility response as actual external measurements.

## ListenerSet integration

The chart defaults certManager.listenerSetName to hosting-sites. ListenerSetReconciler creates a dedicated ListenerSet when the first certificate is ready, attaches it to the configured Gateway and creates per-domain HTTPS listeners and HTTPRoutes to apache-baseline. It patches only the configured, labelled, exclusively managed set using resourceVersion checks, and deletes it with UID/resourceVersion preconditions when the last eligible certificate disappears. Existing unowned ListenerSets are refused, not adopted. Kubernetes requires namespace-scoped create permission; named LIST/WATCH uses a metadata.name field selector and get/list/watch/patch/delete remain restricted to the configured name. Global HTTP-to-HTTPS redirects remain external Gateway infrastructure. Certificate readiness is distinct from routing readiness or successful end-user HTTPS.

HostingPanel owns Certificate resources independently of the Gateway certificate-shim. The dedicated ListenerSet annotates every managed listener name with `cert-manager.io/ignore-tls-listeners` (cert-manager 1.21+ required when the ListenerSet shim is enabled). The annotation is reconciled atomically with listeners, including existing sets. Other annotations and Gateways are untouched. On older cert-manager versions, upgrade before relying on this exclusion. Existing shim-created duplicate Certificates are not automatically removed by this annotation; verify their ListenerSet owner references and Secret ownership before operator cleanup. Never delete the shared TLS Secret as part of duplicate cleanup. The parent Gateway must independently allow ListenerSets from the hosting namespace.

Domain deletion removes ACME intent and schedules cleanup; the cleanup worker waits for dependent website/DNS operations and, when enabled, certificate removal. TLS Secret cleanup depends on cert-manager owner-reference configuration; it is not guaranteed by HostingPanel alone. See [reconciliation](reconciliation.md).

Source map: KubernetesCertificateProvider.cs, KubernetesCertificateProvider.Status.cs, CertificateReconciler.cs, ListenerSetReconciler.cs; provider library CertManagerClient.cs, CertificateSnapshot.cs, KubernetesResourceClient.cs. Install cert-manager and required Gateway API support separately; HostingPanel does not install those controllers or their CRDs.

Website HTTPRoutes always attach to the configured Gateway HTTP listener, independently of SSL intent or certificate readiness. The certificate reconciler owns the separate HTTPS routes. Existing website routes are reconciled on controller startup. Global Gateway redirects are not managed by HostingPanel.

## Installer panel bootstrap TLS

The standalone installer creates a namespace-local self-signed Issuer and an explicit panel Certificate, independent of public DNS or ACME availability. The external Gateway references the panel TLS Secret directly; no ACME gateway-shim annotation is used. Domain certificate reconciliation is unchanged. The operator can promote the panel Certificate to the Let's Encrypt ClusterIssuer after public DNS and ingress are ready, without changing the Gateway TLS Secret reference. See [installer documentation](../installer/README.md).

CI includes an isolated kind Kubernetes API LIST/WATCH test with a fixture Certificate CRD, covering create, update, disconnect, relist and delete. This does not test actual cert-manager issuance or Gateway serving convergence.

A separate kind integration workflow installs the installer-pinned cert-manager and Envoy Gateway versions, bootstraps a private CA using a SelfSigned Issuer, and runs the actual CertificateReconciler with isolated SQLite intent. It checks issuance, ListenerSet and HTTPS route convergence, CA-trusted HTTPS with hostname verification, preservation of a second domain and cleanup. It does not exercise ACME challenges, public DNS, the UI/API mutation path or production storage. Results must be verified in CI.

The serving integration test also deletes a managed HTTPS route and the ListenerSet and checks event-driven restoration without a desired-state mutation. Ordinary status updates are excluded from reconciliation by fingerprint contract tests.

## Subdomain certificates

On creation, a subdomain copies the parent's ACME enablement, explicit provider selection and key type into its own persisted intent. It requests its exact hostname, not a wildcard, and does not inherit skipped DNS names, secrets or DNS solver credentials. A disabled parent does not implicitly enable issuance. Subsequent child settings are independent; parent changes do not cascade. The existing account/package/default issuer resolution remains in effect when no explicit provider is selected. After committing child and certificate intent, the service enqueues the child ID in CertificateWork. The existing CertificateReconciler creates a separate Certificate and, once ready, the existing ListenerSetReconciler provides HTTPS routing. No additional polling is introduced. Issuance may wait for DNS and HTTPRoute convergence; cert-manager owns challenge retries and renewal. Live issuance and deletion integration remain to be validated.
