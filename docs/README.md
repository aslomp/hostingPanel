# Documentation map

Current behavior is described in the following pages. Start here, then verify against code and tests on the branch you are changing.

| Document | Purpose |
| --- | --- |
| [Architecture](architecture.md) | Host, project boundaries, request and reconciliation flow |
| [DirectAdmin backend](directadmin.md) | Upstream ownership, capability map, API transport and workflows |
| [Capabilities](capabilities.md) | Backend support and known limitations |
| [Configuration](configuration.md) | Source of truth, settings and secrets |
| [DNS](dns.md) | Resolution, zone lifecycle and PowerDNS |
| [Web hosting](web-hosting.md) | Website capability and lifecycle |
| [Kubernetes hosting](kubernetes-hosting.md) | Shared Apache, PHP, storage and deployment |
| [Installation](installation.md) | Running the unified host |
| [Persistence](shared-sqlite-persistence.md) | SQLite ownership and schema extension |
| [Localization](localization.md) | UI translation conventions |
| [Decisions](decisions/README.md) | Architecture invariants and rationale |
| [Plans](plans/README.md) | Work not necessarily implemented |
| [Archive](archive/README.md) | Historical material, not current instructions |

[AGENTS.md](../AGENTS.md) is the repository-wide engineering guide. Documentation is source-reviewed against main at 510684cdb965011b592e2803c8b7c274143e01e6; this is not a live-cluster acceptance report.
