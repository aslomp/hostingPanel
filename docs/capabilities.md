# Backend capability inventory

Source-reviewed against main at 510684cd. "Implemented" means code exists, not that production acceptance has been demonstrated. DirectAdmin forwarded routes depend on upstream version and permissions; inspect actual responses. This is not a complete endpoint-by-endpoint conformance matrix.

| Capability | DirectAdmin | Kubernetes |
| --- | --- | --- |
| Authentication and account hierarchy | Upstream session and permissions | SQLite accounts, password verification and visibility |
| Account create/delete, package, limits, suspension | Legacy adapters; package choices read from DirectAdmin | Desired-state service; package choices read from authorized SQLite inventory; deletion blocked while dependent resources exist |
| User overview | Legacy account/config/usage adapters | Local account/package overview; strict modern user schema not implemented |
| Impersonation | Backend-verified delegation | Not implemented |
| Domain and website CRUD | Upstream domain lifecycle | SQLite intent, website reconciliation |
| DNS zones and records | Upstream legacy adapters | SQLite CRUD and PowerDNS reconciliation when enabled |
| Automatic DNS for new domains | Upstream | StageDomainDnsAsync; deferred retries for missing settings |
| Account nameserver management | Read-only own profile via upstream session config; editing not implemented | Existing IP/reseller/default resolution; profile displays effective nameservers (read-only) |
| TLS / ACME | Upstream operations | domain-keyed event-driven cert-manager Certificate reconciliation without idle polling, independent routing watches and automatic dedicated ListenerSet lifecycle; shim opt-out requires cert-manager 1.21+ |
| Database inventory | Upstream | SQLite inventory; MariaDB create/delete when configured, otherwise 501 |
| File access | Upstream operations where supported | FTP operations explicitly return 501; SSH/SFTP not established |
| Tickets | Upstream | Owner-scoped SQLite tickets/replies/status and recipient messages; full support routing not established |
| General modern /api compatibility | Explicit forwarding | Compatibility handler returns 501 for unsupported calls |

## DirectAdmin server administration

Official server settings, TLS/ACME, sessions, service actions, updates, system information, security and packages are routed to DirectAdmin; upstream version and authorization determine success. Legacy services implement resource operations separately. See the [DirectAdmin backend guide](directadmin.md) for concrete endpoints, services and UI boundaries.

See [support and realtime](support.md), [certificates](certificates.md), [authentication](authentication.md) and [reconciliation](reconciliation.md) for the implementation boundaries.

## Validation boundaries

- Shared Apache and dedicated account PHP workloads, NFS permissions and tenant isolation require actual cluster testing. Legacy shared PHP workloads require operator retirement after migration.
- Gateway, cert-manager, authoritative delegation and PowerDNS infrastructure are external operational prerequisites as applicable. The bundled PowerDNS deployment uses configurable `powerDns.fsGroup` (default 953) for its non-root init and server containers; persistent storage must support that filesystem group or be prepared with compatible permissions.
- No claim that every Swagger operation has a dedicated UI or Kubernetes implementation.
- Plans are tracked under [plans](plans/README.md); open PRs are not included in current main capabilities.
- Contract tests, fake upstream integration and browser smoke coverage are in tests/ and CI. Live production verification is separate.

- The account inventory and migrated detail pages use cancellable component-owned reads; this is a UI lifecycle improvement, not an expansion of backend capabilities.

- Database overview reads have component-owned cancellation and session guards; this does not extend backend database mutation support.

- Ticket overview refreshes coalesce inbox invalidations and cancel obsolete session reads; backend ticket scope and support-routing limitations remain unchanged.

- Account creation uses only backend-authoritative package choices; Kubernetes matches package names, not numeric IDs. An unavailable package results in a descriptive error rather than accepting arbitrary free-text input.

- Dedicated user and reseller package management uses DirectAdmin legacy package endpoints and Kubernetes SQLite package settings. Kubernetes stores limits but does not yet enforce all of them. Existing untyped packages are user packages; reseller packages must be created explicitly. Package names are unique per owner across kinds in the existing SQLite schema.

- DNS zone inventory reads use component-owned cancellation and session guards, without changing provider authorization or DNS provisioning capabilities.

- Account, database, domain and DNS-zone creation dialogs and DNS record editing share mutation lifecycle state; no additional backend mutation capabilities are implied.

- System information has a dedicated UI operation service; upstream compatibility routes and support limits remain unchanged.

- System service management uses a dedicated UI operation service without expanding upstream or Kubernetes support.

- Server settings use a dedicated UI operation service without adding backend capabilities.

- System package management uses a dedicated UI operation service; upstream compatibility and backend support remain unchanged.

- The optional MariaDB Helm workload provides shared database infrastructure. Kubernetes database mutations use the provisioning account when configured; failures return sanitized API errors and log MariaDB error numbers and SQL states.

Helm-managed MariaDB (Kubernetes only): optional dedicated MariaDB StatefulSet, independent PVC, internal service, separate provisioning Secret and first-initialization account bootstrap. Existing volumes require manual account initialization; deployment/runtime validation is pending.

- Shared Apache configuration is published as immutable revisions on a dedicated direct NFS export (apacheConfigPath on nfsServer, no Apache PV/PVC) by the Kubernetes reconciler; named StatefulSet pods support scoped SDK validation and graceful reload. Live migration and runtime acceptance are pending.

- Optional Stalwart v1 Helm infrastructure (Kubernetes only): single StatefulSet, persistent configuration and data, internal administration service and separate mail service. Mailbox CRUD, DNS automation, quotas and panel API integration are not implemented. See [Kubernetes hosting](kubernetes-hosting.md#mail-infrastructure).
- Kubernetes account PHP-FPM settings: authorized admin/reseller edits image and resource limits; trusted images are managed by admins in BackendSettings. Package defaults and account overrides are stored in existing limits JSON. Runtime acceptance pending; unsupported by DirectAdmin.

- Stalwart v1: optional Kubernetes-only official legacy `CMD_API_POP` API for listing, creating, setting individual quotas and deleting ordinary mail users. DirectAdmin forwards this exact endpoint to its upstream using caller credentials. Only list, create, quota modification and single-user deletion are implemented for Stalwart. Password reset, alias/forwarder changes, aggregate package quota enforcement, DNS publication and domain lifecycle integration are not implemented. See [mail infrastructure](kubernetes-hosting.md#mail-infrastructure).

- The additive `/hosting/mail` JSON routes reuse the Stalwart mail provider and are separately documented at `/openapi/hosting-mail.json`. DirectAdmin supports the official legacy passthrough and additive typed mailbox inventory, create, quota modification and delete.

- MudBlazor mailbox frontend: accessible domain selection, mailbox inventory, create, quota edit and confirmed delete using the shared mail capability; DirectAdmin typed mail management uses CMD_API_POP with upstream read-back. Live browser verification pending.

- Stalwart mailbox creation enforces the owning account's `nemails` package limit and account override across all current hosted domains, including suspended domains. Missing entitlement denies creation. Single-panel-process writes are serialized; external writers, orphaned domains, aggregate disk limits and lifecycle reconciliation remain outside this guarantee.

- Automatic Stalwart mail DNS: first mailbox/forwarder create request durably schedules import of generated MX/SPF/DKIM/DMARC into the existing owned zone. Conflicting records are retained and block import; the existing DNS worker publishes staged records. Requires PowerDNS and Stalwart management configuration. Existing domains are imported on subsequent create requests; optional scheduled refresh of previously scheduled mail domains is separate from the event-driven DNS worker (no idle polling). Native DKIM rotation supports automatic PowerDNS provider registration or a preconfigured Stalwart DNS provider ID and delegates selector TXT records to Stalwart; live validation pending.

- Official legacy `CMD_API_DNS_ADMIN`: original GET/POST request forwarding for DirectAdmin; Kubernetes admin-only exists/create/add backed by existing DNS desired-state operations. Legacy mutation responses distinguish accepted asynchronous publication in details; authoritative DNS read-back is separate. Other legacy DNS actions are not implemented for Kubernetes. Modern pinned Swagger unchanged.

- Clean-server installer: local Docker build services are stopped and disabled after image import only when installer ownership was established before package installation. Existing runtimes are preserved; K3s embedded containerd remains active. See installer README.

- Website, DNS and certificate reconciliation share a coalescing channel loop; certificate work preserves domain keys and LIST/WATCH.

- Website reconciliation has no idle timer; committed changes and resource watches trigger staged NFS, PHP and Apache work. External NFS mutations are not automatically detected.

- Native Stalwart mail DNS configures per-domain publication of MX/SPF/DKIM/DMARC and DKIM management; the panel reads live mail RRsets, preserves them during ordinary zone reconciliation, and permits explicit TXT and MX edits. Manual periodic import is opt-in and disabled in native mode. Other mail record types are not delegated. live validation pending.

Domain settings `mailAutoPublish` and `mailAutoRotate` are independent desired-state flags, default true. The existing authorized `/hosting/domains/{domain}/settings` operation accepts ON/OFF. Existing mail domains enqueue durable work on changes; domains not yet provisioned retain the flags for first mailbox creation. Turning publication off sets Stalwart DNS management to Manual without deleting records. Turning rotation off sets DKIM management to Manual. Stalwart cannot actually rotate keys with manual DNS management, even when automatic DKIM key management is selected. External mail providers should use manual publication. Existing TXT and MX edits remain allowed. DirectAdmin is unchanged.

- Kubernetes watch streams have independent resourceVersion cursors and reconnect lifecycles; cleanup waits on dependency invalidations, and actual execution failures use capped exponential backoff. Per-resource retry isolation and live reconnect validation remain outstanding.
- Subdomain UI uses `ISubdomains`; both Kubernetes and DirectAdmin register providers. The legacy CMD_API_SUBDOMAINS endpoint supports list, create and single deletion with contents=yes in Kubernetes; DirectAdmin forwards upstream.

Kubernetes subdomain ownership uses an explicit provider-owned `SubdomainBindings` relation (child HostedDomain, parent HostedDomain, normalized label). The parent determines the hosting account; the caller cannot specify an owner. The scoped capability authenticates and checks the existing account hierarchy before inventory or mutation validation. Nested subdomains are not supported in this stage. Generic domain deletion refuses linked children and parents; remove children using the subdomain workflow first. Create persists the child and relation atomically with website intent, then signals the existing website channel. The worker provisions the separate full-hostname documentroot, shared Apache vhost, HTTPRoute and account PHP runtime. DNS uses a CNAME in the owned parent zone; missing parent zones defer durable domain-dns work until DNS invalidation. Subdomain creation copies the parent's certificate enablement, provider and key type into independent child ACME intent, queues the child ID after commit, and uses the existing CertificateReconciler. Subdomain deletion stages website, certificate, DNS and storage cleanup using existing workers. HTTP 202 indicates accepted desired state, not DNS or serving readiness.

- Optional SFTPGo Helm infrastructure: one StatefulSet, persistent state and host keys, separate internal management service and SFTP service. NFS is deliberately read-only pending UID/GID and tenant-isolation validation. No account provisioning or customer access is implemented yet.

- SFTPGo internal management client supports admin token exchange, exact user lookup, create/update/delete with read-back and disabled-by-default managed users. No public API, account lifecycle wiring or customer activation yet. Internal HTTP transport requires enforced NetworkPolicy and a trusted cluster network; TLS remains a deployment follow-up.

- Kubernetes SFTP account UI stores owner-scoped pending account definitions and public keys in SQLite; the existing ftp package/account limit is enforced on creation. No upstream activation or reconciliation yet. DirectAdmin SFTP management is not supported by this UI.

- Pending SFTP accounts support an optional relative home directory, stored in the provider schema. Absolute paths, empty path components, traversal and unsupported characters are rejected. Storage existence, ownership and symlink confinement must be verified before activation; no upstream user is activated by this feature.

- SFTP directory preflight reuses NFS provisioning ownership and permission checks at account creation and before disabled-user provisioning. It rejects missing directories, symlink components, root ownership and mismatching descendant UID/GID; no storage changes are made. This is not a runtime confinement guarantee against concurrent renames. Activation remains disabled pending live filesystem isolation and ownership validation.

- Official legacy CMD_API_SSH_KEYS GET/POST is forwarded unchanged to DirectAdmin using caller credentials. Kubernetes rejects that operation (system SSH keys are not virtual SFTP accounts). Separate /hosting/file-access/sftp routes expose the owner-scoped pending SFTP account capability; no pinned Swagger changes or FTP password compatibility translation.

- SFTPGo reconciliation consumes durable account, owner and package operations through the shared channel loop; initial startup reconciles existing rows. Users remain disabled pending runtime filesystem validation. No idle polling.

- SFTP account overview displays pending, failed or completed-disabled provisioning status from the outbox and refreshes on existing invalidations. Removal requests upstream connection termination and verifies no matching connections remain. Zero entitlement revokes users. Live validation pending; activation remains blocked.

SFTPGo remains disabled by default; enabling it now defaults to write mode and public-key account activation. Set `sftpgo.enableWrites: false` to stage disabled users on read-only storage. Production isolation acceptance remains outstanding.

DirectAdmin native SSH authorized-key management: `ISshAuthorizedKeys`, `/hosting/file-access/ssh-keys`, caller-scoped upstream read/authorize/revoke with read-back. Does not grant SFTP-only confinement; server SSH policy remains authoritative. Virtual SFTP accounts remain Kubernetes-only.

File transfer compatibility: DirectAdmin uses native FTP; Kubernetes exposes domain-scoped FTP compatibility reads/deletes and public-key-only creates/updates through the SFTP outbox. The legacy FTP controller forwards to the selected backend. Kubernetes does not accept FTP passwords or claim to implement the FTP wire protocol.
