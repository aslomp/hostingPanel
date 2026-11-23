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
- Other /api/* routes — **501 NOT_IMPLEMENTED** instead of forwarding incompatible requests to legacy endpoints.
- Existing Blazor dashboard and users page use modern endpoint.
- Account modification, user details, DNS, hosting packages, TLS, databases, etc. are **not implemented yet**.

## Compatibility invariants

1. Each route's HTTP method, JSON field names, response codes, permission scopes and validation must match the pinned modern Swagger contract.
2. An endpoint is advertised by the pinned full specification even before our implementation is available; use explicit 501 for unimplemented operations. Track implementation coverage separately.
3. Do not return fake responses or silently proxy a modern endpoint to the old DirectAdmin.
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


## Eén globale mutatieschakelaar

`HostingPanel:EnableMutations` (omgevingsvariabele `HostingPanel__EnableMutations`) is de **enige** globale write-switch. Standaard `false`.

- `false`: alle uitvoerende POST/PUT/PATCH/DELETE-verzoeken naar de API worden door middleware met HTTP 409 `MUTATIONS_DISABLED` geblokkeerd. Het niet-uitvoerende endpoint `POST /hosting/domains/preview` blijft toegankelijk.
- `true`: de globale blokkade vervalt; elke geïmplementeerde mutatie kan worden uitgevoerd mits de authenticatie, DirectAdmin-rechten, invoervalidatie en eventuele expliciete bevestiging slagen. Niet-geïmplementeerde operaties blijven HTTP 501 retourneren.
- De domeinnaamvalidator accepteert normale domeinen (bijvoorbeeld `example.com`, `customer.co.uk`) én fictieve `.xyz1`-testdomeinen. Alleen de geautomatiseerde testfixtures gebruiken standaard `.xyz1`.
- `POST /hosting/domains/execute` verwerkt `create`, `modify` en `delete`. Wijzigen en verwijderen vereisen dat het domein al bij het aangemelde account bestaat; aanmaken vereist dat de naam nog niet in diens domeinlijst voorkomt. De exacte domeinnaam moet expliciet worden bevestigd.
- De vlag verleent op zichzelf geen rechten. DirectAdmin blijft de authorisatie uitvoeren met de credentials van de aanroepende gebruiker.

**Let op:** als de vlag aan staat, mogen alle bestaande geïmplementeerde mutaties ook **echte productiedomeinen** wijzigen. Zet de vlag uitsluitend aan in een passend afgeschermde omgeving.

## Domain detail and editing

- The domain list links to `/domains/{domain}`. The new Razor page displays the current domain configuration and all supplementary legacy fields from `CMD_API_ADDITIONAL_DOMAINS?action=view&domain=...`.
- `GET /hosting/domains/{domain}` requires a valid authenticated DirectAdmin user and verifies that the requested domain belongs to the current DirectAdmin account before reading settings.
- `PUT /hosting/domains/{domain}/settings` accepts `{"setting":"ssl","value":"ON"}` for a single, explicitly supported setting: `ssl`, `php`, `cgi`, `bandwidth`, or `quota`. It checks membership and actual availability in the returned domain config, and the global `HostingPanel:EnableMutations` gate applies.
- Single-property saves use `CMD_API_DOMAIN` with `action=modify&only_affect=<field>`, which avoids accidentally turning unrelated unchecked legacy options off. All updates use the original user's login key and DirectAdmin enforces permissions.
- Legacy domain settings may expose extra keys depending on the DirectAdmin release. The interface displays those as informational fields, but does not offer speculative editing for unknown parameters.
- The detail endpoint is scoped to the authenticated account. When an admin lists another user's domains, the original credentials must also have user-level access to that domain for detail calls; separate impersonation support is future work.

No production mutations occur in CI; contract tests use `hosting-test.xyz1`.

## User details and assigned package

- `GET /hosting/users/{username}/overview` composes a practical user detail response using `CMD_API_SHOW_USER_CONFIG?user=...`, `CMD_API_SHOW_USER_USAGE?user=...`, and `CMD_API_PACKAGES_USER?package=...` (the package name comes from the actual user config).
- The page `/users/{username}` displays the account settings, assigned hosting package, package limits and usage from the adapter, without depending on a fully populated strict `web.userConfig` or `web.userUsage` schema. Missing optional data is shown as unavailable rather than replacing the entire page with HTTP 501.
- The pinned modern `/api/users/{username}/config` and `/usage` contracts remain separate; contract completeness is evaluated there, not applied as a UI rendering prerequisite.
- The API strips fields with credential-related names from the account, usage and package details. Package and usage fetch failures do not hide successfully loaded account settings.
