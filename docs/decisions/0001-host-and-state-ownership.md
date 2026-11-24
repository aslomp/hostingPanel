# ADR-001: unified host, backend capabilities and canonical state

Status: recorded existing architecture, 2026-11-24.

Context: HostingPanel supports an existing DirectAdmin server and a Kubernetes-native backend. Earlier iterations used two hosts and a universal backend facade; duplicated configuration risks diverging from actual provisioning.

Decisions:
- One Web host, independent capability interfaces and backend-owned permissions.
- DirectAdmin is authoritative for its state; Kubernetes uses the shared SQLite model and provider-owned extensions.
- One persisted public Kubernetes settings record; deployment values are initial defaults. Keep DNS resolution on existing IP/name assignments and defaults; independent nameserver editing must not change IP entitlement implicitly.
- Use Kubernetes .NET SDK in application code, shared Apache plus per-domain vhost configuration, existing DirectAdmin-compatible storage layout.
- Keep plans distinct from implemented behavior and desired state distinct from observed readiness.

Consequences: no universal backend contract, duplicate account DNS storage, per-domain Apache workload, speculative upstream API or silent schema change. Missing UI should expose existing capabilities first. Changes to these decisions require an explicit rationale.
