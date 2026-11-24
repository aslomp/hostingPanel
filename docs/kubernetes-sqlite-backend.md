# Kubernetes backend: SQLite control-plane foundation

The public HTTP routes and Blazor client use **IHostingBackend**. Controllers do not own any DirectAdmin transport, credentials, or provider-selection logic. Choose the implementation at startup through `HostingPanel:Backend` (`DirectAdmin` by default, or `Kubernetes`). No Kubernetes operation falls back to DirectAdmin.

## First boot

```bash
export HostingPanel__Backend=Kubernetes
export ConnectionStrings__HostingPanel='Data Source=/var/lib/hostingpanel/hostingpanel.db'
export HostingPanel__Bootstrap__Username=admin
export HostingPanel__Bootstrap__Password='USE-A-UNIQUE-LONG-SECRET'
dotnet run --project src/HostingPanel.Api
```

The bootstrap account is created **only when the SQLite account table is empty** and both credentials are configured. Never store the bootstrap password in `appsettings.json` or commit it. On subsequent restarts, the existing account is reused. Account secrets are salted PBKDF2-SHA256 verifiers (not plaintext). Database-login credentials must be provisioned by a Kubernetes Secret implementation; the panel database stores **only Secret references**.

Use a persistent, single-writer volume for SQLite. Do not mount the same SQLite file for concurrent API replicas across network storage. Initial database creation uses EF Core `EnsureCreatedAsync` (baseline development schema); migrations and upgrades are a prerequisite before production. SQLite's journal mode is WAL and a busy timeout is configured. Protect the SQLite file, WAL file and backups with the same privileges as other hosting management state.

## Persistent entities

| Logical resource | Table(s) |
|---|---|
| Accounts and hierarchical ownership | Accounts |
| Hosting packages and resource limits | Packages, Accounts.LimitsJson |
| Domains and desired settings | Domains |
| DNS zones and record sets | DnsZones, DnsRecords |
| Customer database inventory | Databases |
| Database users and privileges | DatabaseUsers, DatabaseGrants |
| FTP/SFTP identity metadata | FileAccessAccounts |
| Certificate requests and Kubernetes Secret references | Certificates |
| Kubernetes resource identity and observed generation | ResourceBindings |
| Tickets, replies and messages | Tickets, TicketReplies, Messages |
| Panel configuration | Configuration |

This is **desired-state data**, not proof that Kubernetes resources or real databases were created, that certificates were issued, or that DNS was published. The future Kubernetes reconciler owns observed state, runtime provisioning, retry policy, failure conditions, and cleanup. Never write generated passwords, private keys, Kubernetes tokens or kubeconfig contents into SQLite.

## Current API behavior

SQLite-backed account discovery, account updates, domain discovery/mutations, domain settings, DNS zone and record CRUD, database inventory, ticket management, and message inbox are implemented through the Kubernetes provider.

The following remain explicitly unimplemented (HTTP 501 or 501 result) and **must not** be considered successful provisioning: general modern DirectAdmin Swagger routes (`/api/*`), database credential creation/deprovisioning, FTP account operations, impersonation, and strict modern user-details translation. The corresponding normalized SQL tables are ready for their implementations.

The current Kubernetes backend does **not** reconcile `Deployment`, `Service`, `Ingress`, `Gateway`, `Certificate`, `Secret`, `PVC` or database workloads yet. Do not deploy the Kubernetes provider to production hosting before implementing those reconcilers, migrations, failure-state visibility, provisioning idempotency, and tests against a real cluster.

## Backend dependency boundary

```text
Blazor --> HTTP API controllers --> IHostingBackend
                                    |-- DirectAdminBackend --> DirectAdmin transport
                                    `-- KubernetesBackend --> SQLite desired state
                                                              --> (future) Kubernetes reconciliation
```

Provider selection is performed once in API dependency injection. DirectAdmin's HTTP client and legacy adapters are registered **only** for the DirectAdmin provider.
