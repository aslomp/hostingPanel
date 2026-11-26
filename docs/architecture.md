# Current architecture

HostingPanel is a single .NET 10 ASP.NET Core application (HostingPanel.Web), hosting Blazor Server/MudBlazor, HTTP controllers, authentication/session handling and composition. The selected backend is registered once in Program.cs using HostingPanel:Backend (DirectAdmin or Kubernetes).

```mermaid
flowchart TD
 UI[Blazor components] --> OPS[Operation services]
 OPS --> C[Capability contracts / HTTP controllers]
 C --> DA[DirectAdmin backend]
 C --> K[Kubernetes backend]
 DA --> D[DirectAdmin HTTPS API]
 K --> DB[(SQLite desired state and outbox)]
 DB --> R[Website / DNS / certificate reconcilers]
 R --> SDK[Kubernetes .NET SDK]
 R --> PD[PowerDNS API]
 SDK --> A[Shared Apache + PHP runtimes]
 SDK --> G[Gateway API and cert-manager]
```

Typed UI operations resolve capabilities in independent per-call BackendOperationScope instances to avoid retaining EF Core contexts across Blazor circuits. Some settings/compatibility operations still use a same-host HTTP client (hosting-api); this is transitional, not a separately deployed API application. See [web UI](web-ui.md). Both compatibility and settings operations share PanelHttpTransport for same-host HTTP, caller identity and response disposal. CompatibilityApiClient remains restricted to explicit compatibility routes, while SettingsOperations owns fixed /hosting settings routes. Backend authorization remains authoritative.

| Assembly | Ownership |
| --- | --- |
| HostingPanel.Web | UI, controllers, authentication session, DI |
| HostingPanel.Backends.Abstractions | Independent capability interfaces, DTOs, public settings schema |
| HostingPanel.Backends.DirectAdmin | HTTPS transport, legacy adapters, backend services |
| HostingPanel.Backends.Kubernetes | Account authorization, desired-state services, reconciliation |
| HostingPanel.Persistence | Common EF Core model, SQLite initialization, extension contracts |
| HostingPanel.Providers.PowerDNS | Authoritative DNS API integration |
| HostingPanel.Providers.CertManager | Certificate integration and shared KubernetesResourceClient |

Controllers depend on individual capabilities (IAccountCatalog, IAccountMutations, IDNSProvider, IWebHosting, etc.). DirectAdminBackend is a provider-internal facade, not the universal application contract. Existing CLR namespaces may reflect earlier organization; assembly boundaries determine ownership.

## API and authorization

The pinned official DirectAdmin Swagger lives at src/HostingPanel.Web/spec/directadmin.swagger.json and is served by /openapi/directadmin.json. /api routes follow that contract and are forwarded where appropriate. HostingPanel-specific operations use /hosting. Forwarding is not proof of backend support. DirectAdmin receives caller credentials over a fixed HTTPS origin; Kubernetes authenticates locally and applies its own account visibility and mutation rules. Neither controller nor UI is a substitute for backend authorization.

The browser uses cookie authentication and a server-side panel session. The application validates session state and supplies effective credentials to operations. A UI action is not proof of resource permission.

## DirectAdmin backend

DirectAdmin is a full backend, not a compatibility-only stub. Its services own the translation of account/reseller, website/domain, DNS, database, FTP and support operations; server administration and TLS use official upstream routes. DirectAdmin performs the actual changes and supplies authoritative results. The local control-plane database is not a mirror. See [DirectAdmin backend](directadmin.md) for the concrete capability-to-service mapping, authentication, delegation, mutations, API and operational boundaries.

## Desired and observed state (Kubernetes only)

Kubernetes services persist desired resources and reconciliation operations. WebsiteReconciler manages immutable Apache NFS revisions and website routes, shared PHP resources and cleanup; DnsReconciler handles deferred zone staging and PowerDNS publication; CertificateReconciler and ListenerSetReconciler manage certificate intent and TLS listener integration. Reconciliation is asynchronous. A 202 response means accepted intent, not operational availability.

The shared Apache service receives per-domain configuration; do not create Apache workloads per domain. KubernetesResourceClient uses the .NET SDK. PowerDNS publication and cert-manager issuance require their actual infrastructure and configuration.

See [authentication](authentication.md), [reconciliation](reconciliation.md), [certificates](certificates.md), [support](support.md), [configuration](configuration.md), [DNS](dns.md), [capabilities](capabilities.md) and [Kubernetes hosting](kubernetes-hosting.md).
