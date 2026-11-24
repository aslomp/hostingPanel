# TLS and certificate lifecycle

## DirectAdmin

DirectAdminBackend implements ICertificateProvider by forwarding official upstream requests. TlsController forwards server-level TLS/ACME operations. DirectAdmin owns certificate issuance, renewal, configuration and status. Do not provision cert-manager resources for this backend.

## Kubernetes configuration and intent

KubernetesCertificateProvider handles the supported official session ACME and domain TLS route subset. Domain ACME intent is stored in Configuration under Acme:Domain:<domain GUID in N format>. Domain settings and certificate settings are separate; neither contains private keys.

Issuer resolution order: explicit domain Provider, AcmeUserProviders keyed by account username, AcmePackageProviders keyed by package GUID string, then AcmeDefaultProvider. AcmeProviders maps the ID to an existing Issuer or ClusterIssuer and includes staging/wildcard metadata. ResolveIssuer is the canonical resolver. Domain names are the apex plus optional wildcard, excluding SkipDNSNames. DNS solver credentials and EAB configuration belong on the external issuer; unsupported inline fields are rejected. Enabling ACME requires a valid mapped issuer and compatible name selection.

## Reconciliation and observed status

CertificateReconciler runs periodically (30 seconds) when both CertManager:Enabled and KubernetesHosting:Enabled. It applies the desired Certificate for eligible SSL/ACME-enabled domains, observes readiness and removes obsolete managed certificate resources. cert-manager handles actual issuance and renewal. The manual provision route submits a Certificate request and does not prove issuance. The dry-run route summarizes intent/observed coverage; it is not a real ACME validation.

CertificateSnapshot reads Certificate, CertificateRequest, Order and Challenge resources through CertManagerClient; public certificate material and status are used to produce the certificate inventory and ACME log views. No TLS private-key/Secret read is needed for these observations. Do not interpret estimated/placeholder fields in the compatibility response as actual external measurements.

## ListenerSet integration

When configured, TlsListenerSetName must match the Helm-authorized existing ListenerSet name. ListenerSetReconciler manages the reserved hp-tls-<domain GUID> entries, preserves unrelated listeners, uses resourceVersion-based optimistic patching and requires an infrastructure-owned baseline listener. Ready domain certificates drive corresponding HTTPS listeners and HTTPRoutes to apache-baseline. Unrelated listener configuration is not replaced with a newly created ListenerSet. Certificate readiness is distinct from routing readiness or successful end-user HTTPS.

Domain deletion removes ACME intent and schedules cleanup; the cleanup worker waits for dependent website/DNS operations and, when enabled, certificate removal. TLS Secret cleanup depends on cert-manager owner-reference configuration; it is not guaranteed by HostingPanel alone. See [reconciliation](reconciliation.md).

Source map: KubernetesCertificateProvider.cs, KubernetesCertificateProvider.Status.cs, CertificateReconciler.cs, ListenerSetReconciler.cs; provider library CertManagerClient.cs, CertificateSnapshot.cs, KubernetesResourceClient.cs. Install cert-manager and required Gateway API support separately; HostingPanel does not install those controllers or their CRDs.
