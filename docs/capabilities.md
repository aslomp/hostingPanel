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
| TLS / ACME | Upstream operations | cert-manager Certificate reconciliation and optional existing ListenerSet integration |
| Database inventory | Upstream | SQLite inventory; create/delete explicitly return 501 |
| File access | Upstream operations where supported | FTP operations explicitly return 501; SSH/SFTP not established |
| Tickets | Upstream | Owner-scoped SQLite tickets/replies/status and recipient messages; full support routing not established |
| General modern /api compatibility | Explicit forwarding | Compatibility handler returns 501 for unsupported calls |

## DirectAdmin server administration

Official server settings, TLS/ACME, sessions, service actions, updates, system information, security and packages are routed to DirectAdmin; upstream version and authorization determine success. Legacy services implement resource operations separately. See the [DirectAdmin backend guide](directadmin.md) for concrete endpoints, services and UI boundaries.

See [support and realtime](support.md), [certificates](certificates.md), [authentication](authentication.md) and [reconciliation](reconciliation.md) for the implementation boundaries.

## Validation boundaries

- Shared Apache/PHP, NFS permissions and tenant isolation require actual cluster testing; shared PHP is trusted-only opt-in.
- Gateway, cert-manager, authoritative delegation and PowerDNS infrastructure are external operational prerequisites as applicable.
- No claim that every Swagger operation has a dedicated UI or Kubernetes implementation.
- Plans are tracked under [plans](plans/README.md); open PRs are not included in current main capabilities.
- Contract tests, fake upstream integration and browser smoke coverage are in tests/ and CI. Live production verification is separate.

- The account inventory and migrated detail pages use cancellable component-owned reads; this is a UI lifecycle improvement, not an expansion of backend capabilities.

- Database overview reads have component-owned cancellation and session guards; this does not extend backend database mutation support.

- Ticket overview refreshes coalesce inbox invalidations and cancel obsolete session reads; backend ticket scope and support-routing limitations remain unchanged.

- Account creation uses only backend-authoritative package choices; Kubernetes matches package names, not numeric IDs. An unavailable package results in a descriptive error rather than accepting arbitrary free-text input.
