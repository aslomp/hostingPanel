# Backend project boundaries

- HostingPanel.Backends.Abstractions: interfaces, shared DTOs, neutral validation, settings schema. No provider dependencies.
- HostingPanel.Backends.DirectAdmin: HTTP transport, legacy adapters, DirectAdmin services and DI registration. References Abstractions only.
- HostingPanel.Backends.Kubernetes: SQLite entities, EF Core, settings, desired state, reconciliation outbox and initialization. References Abstractions only.
- HostingPanel.Api: HTTP controllers, official DirectAdmin-compatible Swagger, provider selection/composition root.
- HostingPanel.Web: HTTP client and UI, no provider assembly reference.

Existing CLR namespaces are retained for source compatibility. Assembly names define ownership. No provider references another provider or the API host. The API selects a provider through HostingPanel:Backend and delegates requests via IHostingBackend. To add a provider, reference Abstractions, implement the contracts, and register it at the composition root. Public settings use IBackendSettings.

Build each provider directly with dotnet build src/HostingPanel.Backends.DirectAdmin or dotnet build src/HostingPanel.Backends.Kubernetes. The existing Dockerfile builds the API and its transitive references. Contract tests reference compiled assemblies rather than copying provider source.
