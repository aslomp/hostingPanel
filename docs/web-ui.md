# Web UI architecture

HostingPanel.Web is the unified ASP.NET Core/Blazor Server host. MainLayout supplies the MudBlazor theme, popover, dialog and snackbar providers; PanelToolbar, PanelSidebar, PanelBreadcrumb and ImpersonationBanner compose the shell. Feature components are grouped under Accounts, Administration, Databases, Dns, Domains, Files, Profile and Support.

## Operation lifetime and transport

The UI uses AccountOperations, DomainOperations, DnsOperations, DatabaseOperations, TicketOperations and ImpersonationOperations. These use BackendOperationScope.RunAsync to capture effective credentials and create/dispose an independent DI scope per operation. Blazor circuits can outlive EF Core units of work; never inject a long-lived DbContext or retain a scoped backend instance in a component or circuit service. Keep async cancellation and disposal intact.

SettingsOperations and CompatibilityApiClient still use the same-host hosting-api HTTP client, which derives its origin from server listening addresses. CompatibilityApiClient restricts allowed relative paths. Do not introduce raw backend transport in Razor or another independently deployed API. Controllers remain available for external clients.

## Navigation and workflow

NavigationCatalog defines DirectAdmin-inspired menu sections. NavigationContextState is circuit-scoped and defaults to admin; its admin/user switch is presentation only, not a role grant or authorization check. PanelSidebar searches localized section/item labels. A planned menu entry can be a generic read-only NavigationPage or a dedicated component: ip-management, name-servers, file-manager and domain-ssl dispatch to specialized components. A navigation link does not establish a working mutation. On audited main, IP/nameserver server screens use BackendSettings, whose capability is Kubernetes-specific; DirectAdmin must not be assumed to support those screens.

Shared PanelPageHeader, PanelLoading, PanelEmptyState, CrudAddButton, CrudDeleteButton and DestructiveResourceDialogs standardize common workflows. Reuse them rather than inventing per-page CRUD styles. DestructiveResourceDialogs provides a shared confirmation UI; backend confirmation/authorization remains mandatory. See [localization](localization.md) for translations.

## Refresh and identity changes

PanelSession.Changed signals circuit identity changes; components subscribing to it must unsubscribe on disposal and marshal background events through InvokeAsync. HostingOperationNotifications is a separate internal invalidation bus for reconciliation progress; no resource data or credentials are published. Refresh through authorized operation services. A notification does not establish completion or access to a resource.

See [sessions and authentication](authentication.md) and [support and realtime](support.md).

## Read lifecycle

AccountOverview and the user, database, domain and DNS detail pages own a LatestRead instance per component. A newer read cancels the earlier read and only the current route/session result may update the UI. Disposal cancels outstanding reads; a session change clears the previous inventory and reloads under the effective identity. Domain provisioning invalidation has its own read lifecycle. Mutations are not cancelled by superseding reads, and a completion from a changed session must not update the current account overview. Provider authorization remains authoritative.

The database overview owns independent LatestRead lifecycles for the current account and accessible-account inventories. Both reset on identity change and discard stale results; each operation resolves its own backend scope. Partial inventory warnings belong to the same accepted result. Existing mutation semantics are unchanged.
