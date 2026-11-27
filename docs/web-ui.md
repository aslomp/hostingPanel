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

AccountOverview and the user, database, domain and DNS detail pages own a LatestRead instance per component. A newer read cancels the earlier read and only the current route/session result may update the UI. LatestRead reports JSON parsing failures through the existing error callback only while the read and its context are current; obsolete failures are discarded. Disposal cancels outstanding reads; a session change clears the previous inventory and reloads under the effective identity. Domain provisioning invalidation has its own read lifecycle. Mutations are not cancelled by superseding reads, and a completion from a changed session must not update the current account overview. Provider authorization remains authoritative.

The database overview owns independent LatestRead lifecycles for the current account and accessible-account inventories. Both reset on identity change and discard stale results; each operation resolves its own backend scope. Partial inventory warnings belong to the same accepted result. Existing mutation semantics are unchanged.

The DNS zone overview uses LatestRead for its authorized inventory, clears records and partial-inventory warnings on session changes, and cancels reads on disposal. Zone creation/deletion retain existing mutation semantics; feedback and confirmation are tied to the captured session channel.

## Dialog mutation lifecycle

Account, database, domain and standalone DNS creation dialogs and the DNS record editor use component-owned DialogMutation for exclusive submission and error state. The dialog captures the session channel at opening, validates it again after asynchronous form validation and before accepting backend results, and closes on identity changes. Dispatched writes are never cancelled or replayed by the UI. DialogMutation also captures JSON parsing failures while the captured session is current. Administrative pages use OperationFeedback to format raw HTTP results consistently; backend-specific result interpretation remains with the feature and authorization remains with the provider.

SystemInformationOperations owns the fixed system-information endpoint list and formats upstream responses for the system information page. The page uses LatestRead and clears results on session changes. Upstream response bodies remain generic; unsupported responses are preserved.

SystemServiceOperations owns the existing service inventory, inspection, log, action and watchdog compatibility routes. SystemServices uses independent LatestRead instances for inventory and inspection, and DialogMutation for exclusive writes and session-bound feedback. No automatic retries or cancellation of dispatched writes are introduced. Existing backend authorization and exact confirmations remain required.

ServerSettingsOperations owns the fixed server-settings compatibility routes, typed configuration source selection, request construction and JSON shape checks. ServerSettings uses LatestRead and DialogMutation; session changes clear configuration inputs and confirmation state. Existing explicit confirmations, API routes and upstream authorization are unchanged. Dynamic upstream configuration bodies remain JSON.

LatestRead.RunPairAsync centralizes the sequential read boundary for system service inspection and update inventory: it checks cancellation and the captured UI context before dispatching the second read. Both results are applied together only while current.

SystemUpdateOperations owns the fixed system-package compatibility routes and validates package names and transaction IDs. SystemUpdates uses separate LatestRead instances for inventory and transaction logs and DialogMutation for explicit update requests. Session changes clear old data and confirmations; dispatched writes are neither cancelled nor retried. Existing upstream authorization and UPDATE confirmation remain unchanged.

Mailbox management uses MailOperations and optional IMailProvider resolution through BackendOperationScope. The Email accounts menu lists accessible domains, then displays domain-scoped mailbox inventory and quota/create/delete dialogs. LatestRead and DialogMutation protect session transitions. DirectAdmin typed mail management uses the caller's credentials and verified legacy mailbox mutations; Kubernetes mutations remain administrator-only in the provider. Password reset is not exposed.

Domain details include a subdomain inventory and create/delete controls through DomainOperations and the optional ISubdomains capability. Reads use LatestRead with route/session guards; creation uses DialogMutation, deletion requires exact confirmation. A missing provider reports unsupported and disables mutations. Neither backend currently registers ISubdomains; the UI is prepared for provider implementation, not a claim of working provisioning.
