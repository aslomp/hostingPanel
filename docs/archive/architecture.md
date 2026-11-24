> Historical snapshot: not current instructions. See [current documentation](../README.md).

# HostingPanel architecture

## Contract boundary

**Public:** HostingPanel.Api publishes the pinned modern DirectAdmin Swagger 2.0 contract from `src/HostingPanel.Api/spec/directadmin.swagger.json` on `GET /openapi/directadmin.json`.

**Adapter:** The API implements each modern /api/* operation behind a provider boundary. The first implementation translates `GET /api/search/users` into the existing legacy DirectAdmin call `CMD_API_SHOW_ALL_USERS?json=yes` and converts the legacy JSON list into the modern string-array response. The public API does not expose legacy CMD_API_* routes.

**Legacy backend:** A fixed TLS-protected DirectAdmin origin receives per-user Basic credentials/login keys. Legacy-specific protocols and encoding never leak through a modern endpoint. No shared administrator credential is configured.

**Blazor:** HostingPanel.Web connects only to HostingPanel.Api using modern routes; it does not call DirectAdmin directly. User names are provided by GET /api/search/users?limit=1000.

## Implemented and missing

- GET /healthz — service status.
- GET /openapi/directadmin.json — complete pinned Swagger spec.
- GET /api/search/users — modern JSON array, search query `q` and `limit` (default 50, cap 1000); legacy adapter.
- GET /api/users/{username}/config — read-only translation of CMD_API_SHOW_USER_CONFIG, strict required-field check against pinned web.userConfig schema.
- GET /api/users/{username}/usage — read-only translation of CMD_API_SHOW_USER_USAGE with limits from CMD_API_SHOW_USER_CONFIG; legacy MiB is converted into integer bytes.
- These two details routes return **501 LEGACY_FIELDS_UNAVAILABLE** if the legacy server does not provide all required fields. No placeholder values are invented. The Blazor detail view explains that limitation.
- Other /api/* routes — forwarded with the original method, query and request body to the configured DirectAdmin origin; DirectAdmin determines support and authorization. No synthetic local 501.
- Existing Blazor dashboard and users page use modern endpoint.
- Account modification, user details, DNS, hosting packages, TLS, databases, etc. are **not implemented yet**.

## Compatibility invariants

1. Each route's HTTP method, JSON field names, response codes, permission scopes and validation must match the pinned modern Swagger contract.
2. An endpoint is advertised by the pinned full specification even before a local adapter exists; unimplemented routes are forwarded to DirectAdmin, whose response is authoritative. Track local adapter coverage separately.
3. Never invent a successful modern response. Forwarding unknown /api/* operations is an explicit compatibility fallback; it does not guarantee matching modern and legacy response schemas.
4. The legacy adapter must preserve upstream authorization boundaries; never call as a shared administrator.
5. Do not log login keys or embed them in static assets or repository.
6. Before publishing publicly, implement authentication/rate limiting/audit logging, restrict upstream egress and validate operations against legacy production behavior.
7. Swapping the legacy adapter for a Kubernetes-native adapter must not change the public contract.

## Run

```bash
export DirectAdmin__BaseUrl='https://your-existing-directadmin:2222/'
dotnet run --project src/HostingPanel.Api --urls https://localhost:7101
# In another terminal:
export HostingPanel__ApiBaseUrl='https://localhost:7101/'
dotnet run --project src/HostingPanel.Web --urls https://localhost:7201
```

Use development-only trusted certificates. For modern search directly through the API:

```bash
curl --fail-with-body -u 'YOUR_DA_USER:YOUR_LOGIN_KEY' \
  'https://localhost:7101/api/search/users?limit=50'
```

The complete static specification lives in `src/HostingPanel.Api/spec/directadmin.swagger.json`, pinned from the public DirectAdmin demo; changing the upstream legacy server does not change this modern public contract.

## Domeinbeheer — HostingPanel business API

De gepinde moderne DirectAdmin Swagger heeft **geen algemene create/update/delete domains API**. De bestaande moderne `/api/*`-namespace wordt daarom niet uitgebreid met fictieve DirectAdmin-routes.

- `GET /hosting/domains` — domeinen van het aangemelde DirectAdmin-account via `CMD_API_SHOW_DOMAINS`.
- `GET /hosting/domains?user=... ` — domeinen van een geselecteerde gebruiker via `CMD_API_SHOW_USER_DOMAINS`; DirectAdmin controleert de toegangsrechten.
- `POST /hosting/domains/preview` — niet-uitvoerende preview voor domeinen zoals `hosting-test.xyz1` en `web.demo.xyz1`; valideert invoer en toont de legacy-formgegevens die *later* voor aanmaak gebruikt kunnen worden. **Er wordt niets naar de DirectAdmin-server gepost.**

`.xyz1` is een fictieve testextensie en wordt niet als echte DNS-TLD verondersteld. De daadwerkelijke lijst wordt live gelezen, niet door fixtures vervangen. Bestaande accounts en productiedomeinen worden niet gewijzigd.

Echte aanmaak, wijziging en verwijdering verlopen via de apart bevestigde execute-route. De preview is nadrukkelijk geen uitvoerende create-call.


## Mutations and authorization

HostingPanel does **not** implement a global mutation toggle. `HostingPanel:EnableMutations` is obsolete.
Authenticated write requests reach the appropriate DirectAdmin operation; DirectAdmin decides access and validates supported operations. Domain deletion still uses explicit exact-name confirmation to prevent accidental user actions.

The fixed HTTPS DirectAdmin origin, authenticated session, safe path handling, explicit endpoint mappings and syntax checks remain security boundaries. They are not a second admin/reseller/enduser authorization model.

## Domain detail and editing

- The domain list links to `/domains/{domain}`. The new Razor page displays the current domain configuration and all supplementary legacy fields from `CMD_API_ADDITIONAL_DOMAINS?action=view&domain=...`.
- `GET /hosting/domains/{domain}` forwards the authenticated request; HostingPanel does not check membership in an account-domain list.
- `PUT /hosting/domains/{domain}/settings` accepts `{"setting":"ssl","value":"ON"}` for supported fields. It does not require a successful preliminary read or presence of that field in a previously returned config. DirectAdmin decides whether the requested write is permitted.
- Single-property saves use `CMD_API_DOMAIN` with `action=modify&only_affect=<field>`, which avoids accidentally turning unrelated unchecked legacy options off. All updates use the original user's login key and DirectAdmin enforces permissions.
- Legacy domain settings may expose extra keys depending on the DirectAdmin release. The interface displays those as informational fields, but does not offer speculative editing for unknown parameters.
- The detail endpoint uses original or delegated DirectAdmin credentials. No local account hierarchy or role is used to block reads or writes.

No production mutations occur in CI; contract tests use `hosting-test.xyz1`.

## User details and assigned package

- `GET /hosting/users/{username}/overview` composes a practical user detail response using `CMD_API_SHOW_USER_CONFIG?user=...`, `CMD_API_SHOW_USER_USAGE?user=...`, and `CMD_API_PACKAGES_USER?package=...` (the package name comes from the actual user config).
- The page `/users/{username}` displays the account settings, assigned hosting package, package limits and usage from the adapter, without depending on a fully populated strict `web.userConfig` or `web.userUsage` schema. Missing optional data is shown as unavailable rather than replacing the entire page with HTTP 501.
- The pinned modern `/api/users/{username}/config` and `/usage` contracts remain separate; contract completeness is evaluated there, not applied as a UI rendering prerequisite.
- The API strips fields with credential-related names from the account, usage and package details. Package and usage fetch failures do not hide successfully loaded account settings.

## DNS zone management

- `GET /hosting/domains/{domain}/dns` asks DirectAdmin for the zone. It first tries the legacy admin DNS interface and may retry the user DNS interface after an explicit denial or not-found result. This is backend capability handling, **not** a claim about the caller's identity or role.
- `POST /hosting/domains/{domain}/dns` sends an authenticated operation to DirectAdmin. A second endpoint is attempted only after an explicit HTTP 403/404 denial, never after a timeout, ambiguous response, or a legacy application error.
- Neither a successful zone read nor existence of a record in a local snapshot is required to initiate a write. Backend permission and mutation results are authoritative.
- Form encoding remains restricted to supported DNS record syntax and unambiguous legacy selection fields. After success the UI refreshes the zone for presentation, not as write authorization.
- Blazor shows one DNS workflow. There is no admin/user selector or local permission-based control gating.

## Server-wide DNS overview

`GET /hosting/dns/zones?page=1` uses `CMD_DNS_ADMIN` for the server-wide zone catalog. The backend may allow or deny it. Clicking a zone opens the same DNS editor as every other domain; no identity mode is selected by HostingPanel.

The distinct DirectAdmin admin and user DNS wire operations remain implementation details of the adapter, not a HostingPanel account role or permission gate.

## DirectAdmin scopes: authoritative backend, no second authorization model

Account discovery and domain visibility are evaluated by **DirectAdmin**, not a
HostingPanel role hierarchy. API credentials are forwarded unchanged for each
request, including the effective delegated identity when login-as is active.

- `GET /api/search/users`: modern DirectAdmin contract, array of usernames visible through the available legacy account-list operations.
- `GET /hosting/accounts`: richer account catalog with `username`, `role` and `creator`; used to display admin → reseller → enduser relationships. It probes `CMD_API_SHOW_ADMINS`, `CMD_API_SHOW_RESELLERS`, `CMD_API_SHOW_ALL_USERS` and `CMD_API_SHOW_USERS`. An upstream denial means that particular category is not available; HostingPanel does not impose a second role filter.
- `GET /hosting/domains/accessible`: aggregates `CMD_API_SHOW_USER_DOMAINS?user=...` across accessible accounts plus `CMD_API_SHOW_DOMAINS` for the active account. Returns domain names, owners and a partial-result warning when some backend calls fail.
- `GET /hosting/domains?user=...`: direct per-account query, preserving backend authorization.
- Domain details and mutations no longer reject requests merely because a domain is absent from the *current* account's own domain list. DirectAdmin authorizes the selected operation.
- DNS does not probe account roles or expose a scope selector. It requests a backend DNS operation and only retries through another endpoint after a definitive rejection; ambiguous write outcomes are never replayed.
- Impersonation eligibility is validated with DirectAdmin's effective identity response; the user does not need to be locally classified as admin or reseller before the backend is asked.

Account roles and creator values are **display metadata**, not authorization
claims. Mutations retain syntax validation and explicit confirmation for destructive
operations, but no global HostingPanel permission or mutation switch.
The pinned modern Swagger remains the public `/api/*` contract; legacy
differences are internal.

The aggregate catalog is not a backup of account ownership; it is derived from
live DirectAdmin responses. Empty or inaccessible categories remain distinct
from connection/protocol errors, which must be reported.

## Swagger UI — volledige DirectAdmin-specificatie

De API levert de **volledige statisch vastgepinde** DirectAdmin Swagger 2.0 uit
`src/HostingPanel.Api/spec/directadmin.swagger.json` (274 routes):

- `GET /openapi/directadmin.json`: originele volledige JSON, byte-identiek aan de repository.
- `GET /swagger` en `GET /swagger/index.html`: zelfgehoste Swagger UI met alle routes, parameters, modellen en responsecontracten. De UI laadt **uitsluitend** de gepinde JSON; er wordt niets uit de live demo gedownload en er is geen externe CDN nodig.
- **HostingPanel.Web → DirectAdmin API** toont de link naar de Swagger UI van de API-host, ook zonder ingelogde sessie.
- De UI laat zien wat het **beoogde API-contract** is, niet wat al in de legacy-adapter is gerealiseerd. Niet-geïmplementeerde `/api/*`-operaties geven voorlopig `501` terug.

De interactieve Swagger UI is publiek bereikbaar zoals de JSON-specificatie. Bescherm de API-host bij internetpublicatie met passende infrastructuurbeveiliging. Het document bevat geen credentials.
