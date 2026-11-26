# Configuration ownership

| State | Authoritative source | Access / notes |
| --- | --- | --- |
| Active backend | HostingPanel:Backend | Startup composition |
| DirectAdmin hosting resources | DirectAdmin server | Caller credentials, upstream permissions; no local mirror |
| Kubernetes public backend settings | SQLite Configuration, key BackendSettings:Kubernetes | Administrator-only IBackendSettingsAccess |
| Kubernetes accounts, domains, DNS zones/records | Common SQLite model | Backend-owned authorization |
| Runtime website configuration | Domain settings + effective backend settings | Reconciled to Kubernetes |
| PowerDNS and cert-manager connectivity | Deployment configuration and secrets | Never expose credentials through public settings |
| Kubernetes resource identity / status | Actual Kubernetes API and reconciliation records | Distinguish observed state from desired state |
| Panel session | Server-side PanelSessionStore and cookie | Not a resource permission hierarchy |

KubernetesSettingsService.GetEffectiveSettingsAsync reads the saved JSON, filling supported missing keys from deployment defaults. Startup configuration is authoritative only when no persisted value exists (or for missing keys). GET /hosting/backend-settings returns schema, values and revision; PUT validates the complete replacement and checks revision, permissions and IP constraints. Unsupported providers may return 501.

## DirectAdmin configuration

DirectAdmin:BaseUrl and HostingPanel:Backend select a fixed upstream and its adapter. The caller's session credentials authorize operations. Server configuration, nameservers, IP allocation, account settings, packages and hosting resources remain on DirectAdmin. Use the documented upstream operations, not KubernetesBackendSettings or local account rows. See [DirectAdmin backend](directadmin.md).

## DNS and IP settings (Kubernetes)

DnsAutoCreateZones, DnsWebIp, DnsNs1, DnsNs2 and IpAddresses live in KubernetesBackendSettings. Each HostingIpAddress carries Address, PrefixLength, Status, Device, NameServer, Global, Resellers and AssignedUsers. Server administration manages IPs and nameservers through existing BackendSettings screens. Nameserver resolution consumes these existing relationships; they also participate in hosting-IP allocation, so changes cannot be treated as independent nameserver assignments. See [DNS](dns.md).

Do not duplicate nameserver settings in separate configuration rows or account maps. If a new requirement cannot be represented without changing hosting-IP entitlement, document the conflict and agree on the model before implementation.

## Certificates and runtime

AcmeProviders maps logical IDs to existing cert-manager issuers; AcmeDefaultProvider, AcmeUserProviders and AcmePackageProviders provide selection defaults. Domain ACME intent is resolved by KubernetesCertificateProvider. TlsListenerSetName must agree with the Helm-authorized existing ListenerSet. Infrastructure settings (NFS, Gateway, namespace, runtime images, capacity and permissions) are deployment configuration; consult the chart and [hosting guide](kubernetes-hosting.md).

## Secrets and persistence

Use secret references and protected deployment settings for API keys, passwords and TLS private material. Do not add secret fields to IBackendSettings, including nested objects. Local account password hashes are not plaintext passwords. See [persistence](shared-sqlite-persistence.md).

## MariaDB provisioning (Kubernetes)

Configure `ConnectionStrings:MariaDb` using a protected deployment secret (environment variable `ConnectionStrings__MariaDb`). Use a dedicated provisioning account on a dedicated MariaDB instance, with TLS verification, schema creation/deletion, account creation and database-level grant privileges plus read access to the grant inventory. Do not use customer or root credentials. The existing database capability prefixes submitted names with the authenticated account name. No database password is stored in SQLite. Physical DDL and SQLite are not atomic: uncertain outcomes require operator reconciliation before retry. Existing external databases must not be adopted automatically. The official compatibility handler supports initial create-with-user/delete and user/grant inventory plus server info. User and grant inventory reflects managed SQLite state; external drift is not verified. Database list and detail use live MariaDB catalog metadata, including counts of objects whose definer account is missing. The provisioning account must have visibility into the relevant schema catalogs and mysql.user/mysql.db. Quotas, password rotation, configurable grants/hosts and orphan-user cleanup remain unsupported.
