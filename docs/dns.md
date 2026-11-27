# DNS: existing architecture and source of truth

## DirectAdmin

IDNSProvider delegates inventory, zone and record operations to the DirectAdmin adapter. DirectAdmin owns the resource state and permissions. The application must preserve caller identity and distinguish upstream HTTP success from application-level rejection. The official Swagger includes ns1/ns2 in session user config; do not assume the modern profile PATCH supports those fields. Verify the documented account mutation and permissions before exposing an editor.

The existing profile reads nameservers through DirectAdminAccountNameservers; write support is not implemented on audited main. See [DirectAdmin backend](directadmin.md) for the concrete upstream capability map and permission handling.

## Kubernetes domain provisioning

Source: src/HostingPanel.Backends.Kubernetes/Backend/KubernetesProvisioningService.cs, method StageDomainDnsAsync. Called by account/domain creation and by deferred DNS reconciliation.

1. If PowerDNS is not configured, automatic staging is skipped. If DnsAutoCreateZones is false, ordinary automatic staging is skipped.
2. Existing same-owner standalone zones can be linked without rewriting records. Conflicting ownership or domain linkage returns DNS_ZONE_EXISTS.
3. Resolve the domain owner and creator, including newly tracked accounts.
4. Hosting IP selection prefers directly assigned eligible addresses (all direct addresses), then reseller shared addresses, then global shared addresses, then server addresses. When no inventory exists, use DnsWebIp. Non-direct selection prefers DnsWebIp among eligible addresses, then ordinal address order, and takes one.
5. Resolve nameservers independently using the existing configuration in the order below.
6. Stage the zone and A/AAAA records, www CNAME and two apex NS records in SQLite and schedule dns-zone reconciliation. Missing eligible IPs or an empty fallback public web IP yield `NO_ASSIGNED_IP`; malformed selected IPs yield `INVALID_HOSTING_IP`; missing distinct valid nameservers yield `DNS_NAMESERVERS_NOT_CONFIGURED`. These errors are independent; callers can persist deferred domain-dns operations. Do not equate accepted domain creation with DNS publication.

### Nameserver precedence (actual main behavior)

| Priority | Existing source |
| --- | --- |
| 1 | IpAddresses entries with owner.Username in AssignedUsers |
| 2 | Entries whose Resellers contain the owner or creator, or whose AssignedUsers contain the creator |
| 3 | DnsNs1, DnsNs2 |

Concatenate in that order, discard blank/invalid names, remove trailing dots, deduplicate case-insensitively, take the first two. Require two distinct valid hostnames. This is a sequence of candidates, not three mutually exclusive pairs: one account name can be supplemented by a default.

On audited main the profile displays the effective nameservers read-only; it does not implement independent selection or account mutation. The nameserver inventory is already persisted under BackendSettings:Kubernetes. Do not introduce a duplicate account store. AssignedUsers and Resellers also affect address allocation; editing them solely to change DNS can unintentionally change future A/AAAA records. An independent account editor must respect that constraint. Existing records are not rewritten when defaults change.

## Publication and removal

DnsReconciler (registered when PowerDns:Enabled) retries pending domain-dns staging in separate scopes/transactions. It consumes dns-zone operations by resource, reads current desired zone/records, and calls PowerDnsProvider to publish or delete. PowerDNS owns SOA. A completed API operation is not proof of public delegation or propagation.

Kubernetes domain deletion schedules owned DNS, website and cleanup operations; cleanup waits for dependent operations as implemented. Do not orphan or remove another account's resources. Explicit record editing is separate from default nameserver changes.

## Infrastructure

See [PowerDNS deployment](powerdns.md) and [configuration ownership](configuration.md). Use a dedicated managed authoritative server/database, configure public delegation separately, and protect the management API. Do not expose its API key to tenants.

## Official legacy admin DNS API

`GET` and `POST /CMD_API_DNS_ADMIN` accept the documented URL-encoded `action=exists&domain=...`, `action=create&domain=...&ip=...&ns1=...&ns2=...`, and `action=add&domain=...&type=...&name=...&value=...` (optional TTL) operations. DirectAdmin receives the original request unchanged and decides authorization and supported operations. Kubernetes requires authenticated admin accounts and reuses IDNSProvider with its existing ownership checks, validation, SQLite and reconciliation queue. Exists returns `exists=1` or `exists=0` for desired-state zone existence, not a DNS publication guarantee. Mutations use URL-encoded `error=0|1&text=...&details=...`; accepted asynchronous changes explicitly say DNS publication is pending. Other Kubernetes actions and unsupported parameters (including reverse zone creation and rawsave) return 501 rather than silently ignoring them. Existing /hosting DNS routes and the pinned modern Swagger are unchanged. Authoritative responses must be verified separately.

## Event-driven DNS

DnsReconciler runs at startup, on committed DNS invalidations and on retryable failures; there is no idle timer. Dependency-blocked intents remain pending until a subsequent DNS invalidation. The optional StalwartDnsSyncScheduler selects due mail domains in SQLite and commits durable mail-dns jobs before signaling DNS. It does not own PowerDNS publication. Set `stalwart.api.periodicDnsSync: false` to disable periodic imports. Native mail DNS delegates MX/SPF/DKIM/DMARC to Stalwart. PowerDNS reads expose the live reserved RRsets; the panel allows explicit TXT edits, including mail-related TXT, while apex MX stays delegated. The scheduler is disabled for native mode and opt-in for manual imports. TXT is shared. Ordinary zone reconciliation preserves authoritative TXT values; explicit user changes are durable dns-record commands applied to the live RRset, retaining unrelated values. Existing apex MX conflicts block native setup. There is no cross-controller atomic compare-and-swap; concurrent Stalwart updates may race with explicit edits. ACME challenge records remain protected. External changes to panel-owned records require a refresh/event; no external drift watch is provided.
