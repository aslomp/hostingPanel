# Shared HostingPanel SQLite persistence

The API owns one SQLite database configured by `ConnectionStrings:HostingPanel`, defaulting to `Data Source=hostingpanel.db`. It is initialized for every backend, including DirectAdmin.

## Ownership and source of truth

- **DirectAdmin backend:** DirectAdmin remains authoritative for its accounts, domains, packages, DNS, hosting databases, webserver configuration, privileges and authentication. HostingPanel forwards those operations to DirectAdmin and never imports or mirrors these resources in SQLite.
- **Kubernetes backend:** The common database is also used for the backend's desired-state records. The existing account, DNS, domain, database and ticket tables are retained to preserve older Kubernetes installations. The Kubernetes backend owns reconciliation and any provider-specific runtime bindings.
- **HostingPanel itself:** Backend-independent settings and future panel-owned metadata can be stored centrally. Do not store passwords, API keys, private keys or secret material there.

The common schema currently includes tables inherited from the original Kubernetes model for compatibility. Their presence does **not** imply DirectAdmin will populate or consult them.

## Extending the model

Implement `IHostingPanelModelExtension` in a backend and register it with DI as a singleton. It receives EF Core's `ModelBuilder` and can add provider-owned entities. Keep backend tables distinctly named and avoid modifying general entity mappings.

For schema upgrades on an already initialized SQLite file, implement `IHostingPanelSchemaExtension` (also a singleton), defining a stable provider identifier and monotonically versioned SQL migrations. A transaction and `BackendSchemaMigrations` tracking prevent reapplying completed provider migrations.

```csharp
services.AddSingleton<IHostingPanelModelExtension, MyBackendModelExtension>();
services.AddSingleton<IHostingPanelSchemaExtension, MyBackendSchemaExtension>();
```

Any additional tables must be created through versioned migrations. EF Core `EnsureCreated` is used only to maintain the current baseline compatibility; it does not upgrade an existing database. Changes to **core** tables also need an explicit migration strategy before production upgrades.

## Operational limitations

SQLite is intended for a single API writer on a local persistent volume. Do not share the DB file among multiple Kubernetes replicas on network storage. Keep the database and WAL backups consistent. Backend migrations must be backward-compatible for rollback. The hosting API must remain backend-delegated; owning the database must not introduce permission or identity assumptions into controllers.
