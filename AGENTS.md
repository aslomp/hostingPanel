# HostingPanel — engineering instructions

These instructions apply to the entire repository. Read [current architecture](docs/architecture.md), [capabilities](docs/capabilities.md) and the relevant subsystem document before changing code. Plans and historical notes are not descriptions of current behavior.

## Investigate before implementing
1. Start from the current target branch and inspect the repository tree, DI registrations, interfaces, implementations, persistence mappings, callers and tests. An empty code-search result is not evidence of absence.
2. Trace the existing end-to-end workflow: UI -> operation service/controller -> capability -> selected backend -> authoritative state -> reconciliation -> observed result. Identify the source of truth and authorization boundary before proposing changes.
3. Search existing configuration keys, models, migrations, Helm values and previous decisions. Extend existing mechanisms; do not introduce parallel settings, tables, providers, interfaces or provisioning workflows just to expose missing UI.
4. If documentation and code disagree, verify against code and tests, explicitly report the discrepancy and correct the documentation in the same PR. Do not infer completion from a plan, endpoint declaration or UI.
5. State the intended changes and invariants; make cohesive, minimal changes. Do not silently alter unrelated behavior.

## Architectural invariants
- One ASP.NET Core host: HostingPanel.Web contains Blazor and controllers. Use capability-specific interfaces registered via DI; do not reintroduce IHostingBackend as a universal facade.
- DirectAdmin owns its resources and authorization. Use the authenticated caller's credentials; never a shared privileged identity. Do not mirror DirectAdmin hosting state in SQLite.
- Kubernetes owns its desired state and enforces authorization in its backend. Scope every read/write to the authorized account hierarchy; UI visibility is not authorization.
- Preserve the pinned DirectAdmin Swagger exactly. New HostingPanel operations belong under /hosting; never invent upstream /api methods or fields. Verify upstream mutation semantics and authoritative read-back where appropriate.
- Shared SQLite schema belongs to HostingPanel.Persistence; provider-specific extensions use existing model/schema extension mechanisms and explicit migrations. Do not persist plaintext credentials or private keys.
- Kubernetes public settings have one canonical persisted source: BackendSettings:Kubernetes. Deployment configuration supplies initial defaults. Keep secrets outside the public settings contract.
- DNS nameservers are currently resolved from IpAddresses.NameServer, AssignedUsers, Resellers and DnsNs1/DnsNs2. Reuse this resolution in UI and provisioning. Do not create a second account nameserver store, or change hosting-IP entitlement merely to edit DNS. If independent NS selection is required, explicitly resolve that modeling conflict first.
- Use the Kubernetes .NET SDK through KubernetesResourceClient. No kubectl subprocesses in application runtime; administrative CLI examples are allowed.
- Shared Apache workload, not an Apache deployment per customer or domain. Per-domain vhost ConfigMaps are the legacy transport during migration to dedicated platform-managed shared NFS snapshots; see docs/plans/apache-production.md. Keep reconciliation in the Kubernetes backend, without a separate agent. Preserve DirectAdmin-compatible NFS paths. PHP execution has its own trust boundary; never claim shared pools are hard tenant isolation.
- DNS, website, certificate and cleanup work must remain retryable and idempotent. A queued operation or Kubernetes API acceptance is not proof of serving readiness.
- Domain deletion requires explicit confirmation and cleanup of owned DNS, routes, certificates and files. Do not introduce quarantine or delete unrelated data.

## DirectAdmin-specific work
- Read docs/directadmin.md before changing any DirectAdmin capability or compatibility route. Inspect the corresponding service/adapter and pinned Swagger; do not infer support from Kubernetes behavior.
- Preserve original/effective identity and upstream permission decisions. Keep official API forwarding separate from legacy adapters and /hosting workflows.
- Verify legacy response semantics (including application-level errors), scope, partial inventories and independent read-back when appropriate. Never blindly replay ambiguous writes.
- A new UI feature must be evaluated for both backends; document differences and unsupported functionality instead of silently implementing Kubernetes-only behavior.

## UI and maintainability
- Use MudBlazor and existing shared page headers, dialogs, loading/error components and operation services. Keep Razor focused on presentation, not backend-specific transport or provisioning.
- Overview pages show a table, create action above it, clickable detail rows and explicit delete actions with confirmation. Reuse components across user/reseller/profile workflows where semantics match.
- Use English localization keys and maintain Dutch translations. Prefer typed DTOs and capability contracts over raw JSON where appropriate. Preserve cancellation, async boundaries and clear ownership.

## Lifetime, notifications and status
- Use BackendOperationScope for typed circuit operations. Never retain a provider or EF Core context for the lifetime of a Blazor circuit.
- Navigation context is presentation, not authorization. Keep original/effective identity, server-side session and resource authorization distinct; see docs/authentication.md.
- Treat HostingOperationNotifications as invalidation only. Keep ticket polling/SignalR channel lifecycle separate; do not publish credentials or bypass authorized reads.
- Read docs/reconciliation.md and docs/certificates.md before changing lifecycle/status logic. Do not remove deletion markers before cleanup succeeds or claim TLS Secret deletion without verifying owner-reference behavior.

## Verification and completion
- Build the affected providers and Web project with .NET 10. Run contract and API integration suites; add regression tests for changed behavior, including unauthorized access, fallback, failures and no unintended side effects.
- For UI workflows use real browser regression coverage following tests/browser-smoke.py and CI; a rendered page alone is not proof that a form works.
- For Helm changes render and validate both backend profiles. Test with fake upstreams and isolated fixtures; never use production accounts or domains for automated tests.
- Review diff for duplicate state, broken contracts, missing translations and stale docs. Report exact test outcomes and untested runtime assumptions. Do not claim successful merge, build or deployment without evidence.
- Update docs/capabilities.md and relevant subsystem docs in the same PR when behavior changes. Put proposals under docs/plans and durable architectural decisions under docs/decisions. Historical snapshots are not implementation guidance.

## Pull request ownership
- Own the full pull request lifecycle: create the PR, monitor CI, fix failures and merge conflicts, and merge it into main yourself once required checks and reviews pass. Do not leave a merge-ready PR for the user to merge manually unless explicitly requested.
- Respect branch protection and required approvals; never bypass failing checks or force-push main. Verify the merge through GitHub and report the merge commit. If blocked, report the exact reason and any required user action.
