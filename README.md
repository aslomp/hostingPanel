# HostingPanel

[![License: AGPL-3.0-only](https://img.shields.io/badge/License-AGPL--3.0--only-blue.svg)](LICENSE)

[![Buy Me a Coffee](https://img.shields.io/badge/Buy%20Me%20a%20Coffee-FFDD00?logo=buymeacoffee&logoColor=black)](https://www.buymeacoffee.com/aslomp)

HostingPanel is an open-source, backend-independent web hosting control panel built with .NET 10, ASP.NET Core and Blazor Server. It provides a single administration interface and a DirectAdmin-compatible API for managing hosting infrastructure.

Use HostingPanel with an existing DirectAdmin server, or use its experimental Kubernetes backend to provision websites on a Kubernetes cluster with Apache, PHP-FPM, Envoy Gateway and NFS.

## Features

- Responsive, authenticated web interface with administrator, reseller and hosting-account management workflows.
- Account and package details, hosting limits, domain management, DNS zones and records, database inventory, file-access management, and support tickets.
- Server administration screens for system information, services, settings, updates, TLS and security.
- Backend-managed authentication and authorization, with cookie-based panel sessions.
- English and Dutch localization using .NET resource files.
- DirectAdmin-compatible REST endpoints, additional provider-neutral hosting operations, and self-hosted Swagger UI.

Availability of individual operations depends on the selected backend. The interface does not assume that every provider supports every feature.

## Hosting backends

### DirectAdmin

Connect to an existing DirectAdmin installation over HTTPS. HostingPanel delegates operations to DirectAdmin using the caller's credentials, including delegated credentials when impersonation is supported. DirectAdmin remains authoritative for account permissions, resource ownership and hosting configuration.

The provider uses modern API forwarding and legacy adapters where required. No local mirror of DirectAdmin's hosting database is required.

### Kubernetes

The Kubernetes backend manages hosting desired state in SQLite and reconciles website resources through the Kubernetes API.

- Local account authentication, account hierarchy and backend settings.
- Account, domain and DNS records stored in the control-plane database.
- One Apache Deployment, Service and ConfigMap per hosted website. Updating one virtual host does not regenerate every other site's configuration.
- Gateway API HTTPRoutes attached to an existing Envoy Gateway.
- Shared PHP-FPM Deployments and internal Services by PHP version, with separate on-demand pools, persistent Unix identities and TCP listeners per hosting account. PHP 8.6 is the default; legacy PHP 7 is optional.
- NFS-backed website data, separate from Kubernetes-managed web server configuration.
- Directory provisioning with ownership and permission validation, when the storage-side provisioning environment has the necessary authority.
- Persistent reconciliation operations and retry handling for website changes.

The Kubernetes backend is experimental. Website reconciliation is implemented, but full cluster validation, production-grade tenant isolation, DNS server integration, database provisioning, certificate automation and SSH/SFTP management are not complete. Shared PHP requires explicit opt-in and should currently be used only for mutually trusted applications. An accepted desired-state operation does not guarantee a ready website.

See [Kubernetes website hosting](docs/kubernetes-hosting.md) for configuration and operational requirements.

## Architecture

```mermaid
flowchart TD
    UI[Blazor Server UI] --> API[ASP.NET Core controllers]
    API --> B[IHostingBackend]
    B --> DA[DirectAdmin provider]
    B --> K[Kubernetes provider]
    DA --> DAS[DirectAdmin server]
    K --> DB[(SQLite desired state)]
    K --> R[Website reconciliation]
    R --> KA[Kubernetes API]
    KA --> A[Apache per website]
    KA --> P[Shared PHP-FPM runtimes]
    KA --> G[Envoy HTTPRoutes]
    A -->|FastCGI TCP| P
    A --> N[(NFS website data)]
    P --> N
```

The UI and API run in a single ASP.NET Core application. Provider-specific behavior is implemented in separate assemblies behind shared contracts.

| Project | Responsibility |
| --- | --- |
| `HostingPanel.Web` | Blazor Server UI, HTTP controllers, authentication sessions and application composition |
| `HostingPanel.Backends.Abstractions` | Provider interfaces, DTOs and shared contracts |
| `HostingPanel.Backends.DirectAdmin` | DirectAdmin transport, adapters and hosting operations |
| `HostingPanel.Backends.Kubernetes` | Kubernetes desired state, account authentication, website reconciliation and infrastructure provisioning |
| `HostingPanel.Persistence` | Shared SQLite database, EF Core model and provider-specific schema extension points |

`IHostingBackend` includes `IAuthenticationProvider`, `IWebHosting` and `IDNSProvider`, alongside contracts for account management, databases, file access and tickets.

Resource-level authorization is the responsibility of the selected backend. The UI and controllers do not maintain an independent hosting permission hierarchy.

## Getting started

### Run locally

Requirements: Git and the .NET 10 SDK. Configure a trusted HTTPS development certificate for local use.

```bash
git clone https://github.com/aslomp/hostingPanel.git
cd hostingPanel

export HostingPanel__Backend=DirectAdmin
export DirectAdmin__BaseUrl=https://your-directadmin-host.example:2222/

dotnet run --project src/HostingPanel.Web --urls https://localhost:7201
```

Open https://localhost:7201 and sign in with your DirectAdmin credentials. Both the UI and API are served by this application.

### Docker Compose

```bash
cd deploy/compose
cp .env.example .env
# Configure the selected backend and its connection settings in .env.
docker compose up -d --build
```

The default port is 8080, bound to loopback. Configure HTTPS through a trusted reverse proxy for normal browser login and remote access.

### Kubernetes / Helm

Build and publish the single application image to your container registry. The Helm chart supports both backend modes.

For Kubernetes website hosting, configure an existing Gateway API/Envoy installation, a dedicated NFS export, authorized directory provisioning, a persistent SQLite volume and a bootstrap administrator secret.

```bash
cp deploy/helm/values-hosting.example.yaml hosting-values.yaml
# Set your image, NFS, Gateway and secret references in hosting-values.yaml.

helm upgrade --install admin deploy/helm/hostingpanel \
  --namespace hosting --create-namespace \
  -f hosting-values.yaml
```

The hosting profile enables the Kubernetes backend, Apache and PHP 8-FPM. Its registry image is an example override; use an image you have built and published from the corresponding source revision. See the [hosting deployment guide](docs/kubernetes-hosting.md) for prerequisites and storage permissions.

## API

| Endpoint | Purpose |
| --- | --- |
| `/swagger` | Interactive Swagger UI |
| `/openapi/directadmin.json` | Pinned DirectAdmin Swagger 2.0 specification |
| `/api/*` | Explicit DirectAdmin-compatible routes |
| `/hosting/*` | Additional hosting management operations |
| `/healthz` | Application health endpoint |

The published specification describes the DirectAdmin compatibility contract; it is not a guarantee that every operation is implemented by every backend. Unsupported provider operations return an appropriate error rather than synthetic success.

## Development

```bash
dotnet build src/HostingPanel.Web -c Release
dotnet run --project tests/HostingPanel.ContractTests -c Release
dotnet run --project tests/HostingPanel.ApiIntegrationTests -c Release
```

GitHub Actions builds the providers independently and runs contract, API integration, authentication and deployment checks. Contributions should preserve backend independence, explicit API contracts and backend-owned authorization.

## Documentation

- [Kubernetes website hosting and deployment](docs/kubernetes-hosting.md)
- [Helm chart configuration](deploy/helm/hostingpanel/values.yaml)
- [Web hosting contract](docs/web-hosting.md)
- [DNS provider contract](docs/dns-provider.md)
- [Shared SQLite persistence](docs/shared-sqlite-persistence.md)
- [Localization](docs/localization.md)

## License and support

HostingPanel is licensed under the GNU Affero General Public License, version 3 only (`AGPL-3.0-only`). See [LICENSE](LICENSE). This applies to original HostingPanel code unless explicitly stated otherwise; third-party components retain their own licenses.

The project is provided without commercial support, SLAs, or guaranteed maintenance. Donations are entirely voluntary and do not create support obligations.
