> Plan snapshot: verify implementation against current source; this is not a capability inventory. See [current documentation](../README.md).

# Production-grade shared Apache hosting — implementation plan

Status: P0 implementation started; runtime acceptance pending.
Scope: Kubernetes backend only. Preserve DirectAdmin API compatibility and existing /home/<account>/domains/<domain>/public_html layout.

## Architecture decisions
- One shared Apache Deployment and ClusterIP Service, horizontally scalable; every replica serves every virtualhost. Never create Apache deployments per customer or domain.
- Store generated Apache virtualhosts on dedicated platform-managed shared NFS storage, separate from customer content. Retain Envoy Gateway for routing/TLS and cert-manager for certificates.
- Use event MPM and PHP-FPM, not mod_php or CGI. Keep Apache non-root, capability-free, and its NFS mount read-only.
- PHP execution is a separate trust boundary; do not claim shared PHP pools constitute hard tenant isolation. Shared PHP remains explicitly trusted-only until isolation is verified. Evaluate isolated PHP workloads without creating per-tenant Apache.
- Use Kubernetes .NET SDK; no kubectl subprocesses in application runtime. Preserve backend-neutral API contracts; deployment capacity belongs in Helm, provider-specific runtime settings in BackendSettings where appropriate.

## Phase 0 — baseline and safety gates (P0)
- [ ] Capture effective rendered Helm manifests, actual Apache modules/MPM and effective configuration against pinned images in CI.
- [ ] Review storage identities, tenant ownership, least-privilege filesystem permissions and service-account boundaries. Validate existing DirectAdmin directories without recursive permission changes or weakening NFS protections.
- [ ] Define and verify PHP execution and network isolation; refuse untrusted tenancy until acceptance. Assess separate account-scoped PHP execution as an option, not an Apache deployment change.
- [ ] Ensure unknown hosts fail closed; define explicit alias and sensitive-file policies, PHP-disabled behavior and source-code protection.
Acceptance: real httpd config validation, positive HTTP/PHP tests and controlled synthetic tenant-isolation regression tests; no production readiness claim until gates pass.

P0 initial changes: deny unmatched hosts on the public listener, separate pod probes from the public HTTP Service, disable forward proxy and TRACE, reduce server signature, deny sensitive files and non-supported PHP-like filenames, and omit PHP index handling when PHP is disabled. Aliases remain explicit-only; no automatic www alias. Hidden directories are denied except the conventional .well-known directory. These rules reduce accidental exposure but do not replace tenant isolation or application-specific secret management. The shared PHP opt-in remains trusted-only. Effective image/MPM validation, storage and identity review, and controlled isolation acceptance remain open.

See [P0 acceptance evidence](../apache-p0-acceptance.md).

## Phase 1 — predictable Apache capacity (P1)
- [ ] Configure replicas, resource requests/limits and event MPM globally through Helm. Validate relationships among ServerLimit, ThreadLimit, ThreadsPerChild, MaxRequestWorkers and spare threads.
- [ ] Benchmark initial candidate: 32 threads per child, 256 max request workers, ServerLimit 16, StartServers 2, spare threads 64–128, MaxConnectionsPerChild 10000. These are hypotheses, not fixed production defaults. Allow lower settings for small pods; reserve headroom for graceful generations.
- [ ] Bound request/header/body and upstream timeouts; configure keepalive and evaluate NFS sendfile/MMAP settings against actual storage.
- [ ] Establish PHP pool/process resource budgets including total processes, request duration and replica multiplication; coordinate admission with Apache capacity.
Acceptance: configuration checks and load tests demonstrate bounded resource use, predictable saturation and recovery, and no unexpected restarts under representative traffic.

## Phase 2 — reliable virtualhost distribution (P1)
- [ ] Move configuration publication and reconciliation into the existing Kubernetes backend; no separate Apache agent, sidecar, or periodic ConfigMap polling.
- [ ] Publish immutable versioned virtualhost snapshots on dedicated shared NFS storage, writable only by the authorized platform publisher and mounted read-only by Apache. Customer workloads must not be able to modify platform configuration. Keep the last-known-good revision and verify storage semantics in integration tests.
- [ ] Use domain lifecycle events and Kubernetes watches for reconciliation, with initial recovery, debounce and bounded retry. Review reload authorization and replica convergence before selecting the final reload transport; do not grant broad workload execution permissions.
- [ ] Remove per-domain virtualhost ConfigMaps and legacy polling only after migration and rollback tests pass. New Apache replicas must start from the accepted configuration revision.
- [ ] Validate candidate snapshots with real httpd before activation; preserve rollback and serialize graceful reloads. Avoid excessive overlapping generations; ensure staging cleanup and handle deletion.
- [ ] Report active revision and errors per replica. Distinguish desired state, accepted resources, loaded configuration and serving readiness; do not falsely report success.
- [ ] Ensure invalid individual domain changes cannot disrupt previously working sites; reconcile missing resources and configuration drift.
Acceptance: create/update/delete, invalid config, watch disconnect, pod restart, rapid batch changes and multi-replica convergence tests pass; no Apache Deployment rollout for ordinary domain edits.

## Phase 3 — independent runtime scaling and availability (P1)
- [ ] Remove lifecycle coupling between Apache replica changes and PHP runtime rollouts through an independently reviewed, fail-closed access-control design. Do not relax existing controls merely to avoid restarts.
- [ ] Evaluate PHP pool updates and graceful runtime changes with bounded impact.
- [ ] Separate startup, liveness, readiness and config revision status; monitor NFS and PHP separately to avoid cascading restarts.
- [ ] Add graceful shutdown/drain, configurable termination grace period, topology spread and optional PDB/HPA; define sensible multi-replica and single-node profiles.
- [ ] Apply fair admission/rate limits per domain/account through supported Envoy policies, aligned with hosting package semantics.
Acceptance: Apache scale-up/down does not require unrelated PHP rollouts; pod replacement and config changes maintain service within measured availability targets; synthetic noisy-neighbor tests verify fairness.

## Phase 4 — hosting functionality and observability (P2)
- [ ] Review required modules: rewrite, remoteip (trusted proxies only), reqtimeout, headers and private status metrics. Keep TLS at Envoy; avoid redundant compression or protocol features unless justified.
- [ ] Define controlled rewrite/.htaccess compatibility policy and explicit www aliases; do not enable unrestricted overrides.
- [ ] Add structured vhost/account access logs, client identity, response status, latency, bytes and correlation; do not log credentials or sensitive request data.
- [ ] Monitor Apache workers, graceful generations, PHP queue/process saturation, NFS latency, errors and revision lag. Restrict metrics access.
- [ ] Pin tested image versions, document upgrade and rollback procedures, remove obsolete per-site Apache and kubectl descriptions from README/docs.
Acceptance: representative applications function, metrics and logs distinguish tenants, sensitive diagnostics are not public, and docs match deployed architecture.

## Phase 5 — release gate
- [ ] Automated unit tests for manifest/config generation, Helm rendering and validation; integration tests against real Apache/PHP images and Kubernetes.
- [ ] Load and soak tests covering static and PHP traffic, reload churn, large domain counts, storage latency, replica changes and controlled failure recovery.
- [ ] Publish measured capacity, latency, memory, error and availability thresholds for supported deployment profiles before production release.
- [ ] Require all P0 findings closed and documented residual risks reviewed; do not infer isolation from successful basic requests.
- [ ] Verify existing DirectAdmin backend behavior and API contracts remain unchanged.

## Execution and references
Implement in small reviewable PRs, with tests and documentation in each phase. Recheck current main before every implementation step; this plan derives from static inspection, not a live cluster audit.
Primary sources: src/HostingPanel.Backends.Kubernetes/Backend/WebsiteReconciler.cs; deploy/helm/hostingpanel/templates/kubernetes-hosting.yaml; deploy/helm/hostingpanel/values.yaml; docs/kubernetes-hosting.md; README.md.
