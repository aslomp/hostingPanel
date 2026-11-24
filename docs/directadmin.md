# DirectAdmin backend — current implementation

Source-reviewed against main 510684cdb965011b592e2803c8b7c274143e01e6. This is the existing DirectAdmin integration, not a proposed adapter or an alternative deployment of DirectAdmin. HostingPanel is a control panel and API compatibility layer; DirectAdmin remains the system of record and performs the hosting operations.

## Runtime and ownership

Select HostingPanel:Backend=DirectAdmin and set DirectAdmin:BaseUrl to a fixed credential-free HTTPS origin (including port if needed). DirectAdminRegistration registers the request-scoped DirectAdminBackend facade and separate capabilities and services. The named legacy-directadmin HTTP client uses a fixed origin, a 30-second timeout, no automatic redirects or cookie jar. TLS validation remains enabled.

HostingPanel.Web contains the Blazor UI, controllers and session handling in one process. UI operation services may use BackendOperationScope to resolve a capability in an isolated DI scope or the same-host hosting-api transport; compatibility calls use CompatibilityApiClient. Neither the UI nor controllers implement DirectAdmin's hosting permission system.

DirectAdmin owns users, resellers, packages, resource limits, domains, websites, DNS, databases, file access, tickets, server settings, services and certificate configuration. Do not import those resources into HostingPanel's SQLite database, create Kubernetes resources for DirectAdmin operations, or use Kubernetes BackendSettings as a replacement for DirectAdmin settings.

## Authentication, session and account hierarchy

- PanelAuthentication validates login using IAuthenticationProvider.AuthenticateSessionAsync. DirectAdminBackend authenticates against GET /api/session. The panel maintains its own cookie/server-side session; the upstream account credentials are used for requests.
- PanelSession exposes the original and effective identity. ImpersonationOperations invokes IImpersonation before switching; ImpersonationService requires a successful upstream session response confirming both original and effective usernames. No local privilege grant, no shared administrator credential.
- AccountCatalogService discovers accessible accounts through DirectAdmin's account-list and configuration operations (CMD_API_SHOW_ADMINS, CMD_API_SHOW_RESELLERS, CMD_API_SHOW_ALL_USERS, CMD_API_SHOW_USERS and CMD_API_SHOW_USER_CONFIG). Denied inventory branches do not imply the caller lacks access to all other resources.
- Admin, reseller and user are DirectAdmin roles, not a locally administered parallel account hierarchy. Navigation presentation may differ by role; actual permissions belong to DirectAdmin. Account inventory and delegation must not be treated as a blanket authorization grant.

## API boundary: two complementary surfaces

| Surface | Implementation | Rule |
| --- | --- | --- |
| Official /api/* | Explicit controllers, IApiCompatibilityHandler, DirectAdminTransport | Pinned Swagger method/path/body/status contract, upstream response authoritative |
| HostingPanel /hosting/* | Capability-specific controllers/services | Provider-neutral workflows; may adapt documented legacy operations |
| Legacy CMD_* / CMD_API_* | Internal DirectAdmin services/adapters | Never exposed as arbitrary public proxy endpoints |

DirectAdminTransport forwards original method, query, content, authorization/cookies and response status/body, excluding hop-by-hop headers. It rejects unsafe paths and does not automatically follow redirects. A timeout or upstream application error is not permission to replay a mutation. Modern route coverage is not equivalent to complete UI functionality or support by every DirectAdmin version.

Pinned contract: src/HostingPanel.Web/spec/directadmin.swagger.json; /openapi/directadmin.json exposes it. Use the pinned definitions when changing existing official operations; put HostingPanel-specific extensions under /hosting. The legacy API has its own field and response formats; verify them separately.

## Capabilities and concrete source map

Paths below are relative to src/HostingPanel.Backends.DirectAdmin/ unless otherwise stated.

| Capability | Implementation | Upstream operations / responsibilities |
| --- | --- | --- |
| Authentication, compatibility, certificate forwarding | Backend/DirectAdminBackend.cs | /api/session, official API forwarding; DirectAdminTransport |
| Account discovery | Services/AccountCatalogService.cs | Accessible admins/resellers/users, config discovery |
| Account mutations | Services/AccountMutationService.cs | CMD_API_ACCOUNT_USER, CMD_API_ACCOUNT_RESELLER, CMD_API_SELECT_USERS, CMD_API_MODIFY_USER, CMD_API_MODIFY_RESELLER |
| Account overview | Services/LegacyAccountOverviewService.cs | CMD_API_SHOW_USER_CONFIG, CMD_API_SHOW_USER_USAGE, CMD_API_PACKAGES_USER; optional usage/package failures do not discard account data |
| Strict user details | Services/LegacyUserDetailsService.cs | Legacy conversion against modern config/usage schema; missing required fields can return 501 LEGACY_FIELDS_UNAVAILABLE |
| Delegated access | Services/ImpersonationService.cs | Upstream-verified effective identity |
| Domains and websites | Services/DomainManagementService.cs | CMD_API_SHOW_DOMAINS, CMD_API_SHOW_USER_DOMAINS, CMD_API_DOMAIN, CMD_API_ADDITIONAL_DOMAINS |
| Accessible domain inventory | Services/AccessibleDomainsService.cs | Discover accounts and domains; preserve own-domain access and report partial results |
| DNS | Services/DnsZoneService.cs | CMD_DNS_ADMIN, CMD_DNS_CONTROL, CMD_API_DNS_ADMIN, CMD_API_DNS_CONTROL |
| Account nameservers | Services/DirectAdminAccountNameservers.cs | GET /api/session/user-config; current profile read-only, write denied |
| Database inventory/mutation | Services/DatabaseManagementService.cs | CMD_API_DATABASES, CMD_API_DB_USER |
| Cross-account databases | Services/AccessibleDatabasesService.cs | Verified delegation and /api/db-show/databases; partial results |
| FTP | Services/FileAccessService.cs | CMD_API_FTP, CMD_API_FTP_SHOW; account CRUD |
| Support | Services/TicketService.cs | /api/tickets, /api/ticket-requests, /api/messages and legacy ticket write commands |

Adapters under Adapters/ parse legacy account, domain, DNS and database formats and encode supported inputs. Keep transport, validation and translation in the backend; do not duplicate those rules in Razor.

## Account workflows

UserController and ResellerController expose provider-neutral creation, deletion, package/limit and suspension operations. AccountMutationService creates users/resellers using documented forms, validates input, requires exact-name delete confirmation and delegates permission checks upstream. Package, single-field limit and suspension changes use independent configuration read-back before reporting success. An HTTP 200 containing legacy error=1 is not success; an unverified change must not be presented as saved.

The user overview (/users/{username}) combines config, package and usage. Missing optional values are handled separately. Reseller details use modern compatibility operations for config and usage. These views are not an independent local account database. The account profile (/profile on audited main) displays nameservers from the authenticated DirectAdmin session; its editor is not implemented for this backend. User/reseller-specific nameserver editing is a distinct upstream operation that must be verified before implementation; do not invent fields in /api/profile/settings.

## Domain, web hosting and DNS

IWebHosting and IDomains share DirectAdmin's existing domain lifecycle; no separate website entity or reconciliation queue exists for this backend. List own or selected-account domains, retrieve details and update explicitly supported settings. Single-field updates avoid overwriting unrelated legacy flags. Domain creation/deletion runs in DirectAdmin; do not independently provision PowerDNS, Apache, Kubernetes or cert-manager for it.

DnsZoneService combines standalone admin zone inventory and accessible domains. A domain is not necessarily an existing zone; report incomplete inventory rather than inventing records. Zone creation/deletion uses the legacy admin operation. Reads may try the user DNS interface if the admin interface cannot supply a valid zone. Record writes may retry through the alternative interface only after definitive HTTP 403/404; never retry ambiguous writes, timeouts or legacy application errors. DirectAdmin owns zone publication and nameservers. See [DNS](dns.md) for the separate Kubernetes workflow.

## Server administration and official API coverage

These controllers live in HostingPanel.Web/Controllers and generally forward documented modern operations through IApiCompatibilityHandler (certificate requests may use ICertificateProvider).

| Controller | Workflow |
| --- | --- |
| ServerSettingsController | Hostname, timezone, DirectAdmin configuration, database connection and server email configuration |
| TlsController | Server TLS/ACME config, certificate files, obtain, enable, status |
| SessionController / AuthenticationController | Session state, active domain, login-as, session management, password and login/logout routes |
| PackageController / ResellerController | User/reseller packages and reseller config/usage |
| SystemInfoController | System information and usage |
| ServiceController | Services and supported actions |
| SystemUpdateController | Updates, package selection/history and related operations |
| SecurityController | ModSecurity and ClamAV operations |
| DirectAdminController | Remaining explicitly mapped official compatibility routes, including profile and other API families |
| FileManagementController / FileAccessController | File management compatibility and FTP workflows |
| TicketController | Support workflows |
| DatabaseController / DomainController / DnsController / WebHostingController | Provider-neutral hosting operations and applicable compatibility routes |

The corresponding Razor areas are Accounts, Administration, Domains, Dns, Databases, Files, Support and Profile. A route mapping does not guarantee a dedicated complete UI. Email account management and any unverified upstream operation must not be marked complete solely because a route exists.

## Configuration and operating DirectAdmin

Use [installation](installation.md) for the unified host. DirectAdmin's own hostname, services, nameservers, IP assignments, packages and hosting configuration are administered through its API/UI, not the local Kubernetes settings record. HostingPanel's local connection settings select the upstream; credentials belong to authenticated sessions and protected configuration, not committed files.

When changing an operation, verify the upstream API semantics and real permission boundaries for admin/reseller/user. Use fake upstreams for automated regression tests, and authorized isolated test accounts for optional live verification.

## Tests and known limitations

- HostingPanel.ContractTests: ScopeContractTests, SwaggerRouteContractTests, BackendMutationIntegrationTests, DatabaseInventoryIntegrationTests, DnsForwardingContractTests, TicketIntegrationTests, NavigationParityContractTests.
- HostingPanel.ApiIntegrationTests: upstream forwarding and response behavior using fake transport.
- tests/browser-smoke.py and required-login-smoke.py: browser/session regression coverage; CI defines actual execution.
- Source review is not a live DirectAdmin compatibility certification. Legacy responses can vary by version; missing schema fields, partial inventories, permission denials, redirects and ambiguous writes must remain visible rather than becoming synthetic success.

For future changes follow [AGENTS.md](../AGENTS.md), the [architecture](architecture.md) and [capability inventory](capabilities.md).
