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
