# HostingPanel — DirectAdmin-compatible control plane

## Invariants

1. Blazor is a user interface, not the authority for hosting state.
2. The HTTP API is the compatibility boundary. DirectAdmin client integrations may continue using `/CMD_API_*` and `/api/*`.
3. Phase 1: transparent pass-through to a pinned HTTPS DirectAdmin server; upstream performs authentication, permission checks and business actions.
4. Phase 2: endpoint-by-endpoint migration to a Kubernetes-native implementation *behind the same API routes*; the frontend doesn't change.
5. For every migrated operation, retain HTTP status, request semantics, output encoding, JSON shape, and error behavior as observable contracts. Version-specific and undocumented DirectAdmin behavior requires contract tests against the actual target server.
6. Treat admin / reseller / user account boundaries and DirectAdmin login keys as security contracts. Never map the entire API to a shared administrator credential.
7. Prevent SSRF: upstream is configured on the server; callers cannot supply arbitrary URLs.
8. Never use `DangerousAcceptAnyServerCertificateValidator` or log credentials.

## Current implementation

- `src/HostingPanel.Api`: HTTP pass-through for DirectAdmin API routes. Preserves upstream body, status and content type; forwards client Basic credentials. Requests without Basic credentials receive HTTP 401.
- `src/HostingPanel.Web`: interactive-server Blazor UI, containing live user listing and an API browser, styled after the existing HTML prototype.
- Original `index.html`, `pages.js` and `pages.css` remain unchanged on main and in the branch.

This is a first vertical slice, **not** full DirectAdmin replacement. Modern JSON endpoints and legacy endpoint behavior beyond the audited sample still need conformance tests. Cookie/session APIs are not yet supported by the proxy. API security and UI authentication need hardening prior to public exposure.

## Running locally

Requires .NET 10 SDK and a trusted TLS certificate for the DirectAdmin server.

```bash
dotnet run --project src/HostingPanel.Api --urls https://localhost:7101
dotnet run --project src/HostingPanel.Web --urls https://localhost:7201
```

Set `DirectAdmin__BaseUrl` for API, e.g. `https://da.internal.example:2222/`. Set `HostingPanel__ApiBaseUrl` for UI to the API HTTPS origin. On development machines use configured localhost development certificates (`dotnet dev-certs https --trust`); don't turn off TLS validation.

**Production:** run both apps behind TLS, restrict API ingress and authentication, protect against brute-force requests, enforce authorization and logging without secrets, and use stable deployments. The web circuit stores entered login-key credentials only in scoped server memory, with no persistence. This implementation has not been compiled or integration-tested in this environment.

## Migration roadmap

1. Snapshot Swagger from `/static/swagger.json` on the actual DirectAdmin build.
2. Capture and compare status/content-types/response formats for each used legacy endpoint.
3. Implement Domain, Account, Package, DNS, Database, PHP-FPM and TLS as individual business use cases.
4. Introduce provider interfaces under the API so a Kubernetes native adapter can replace the legacy provider.
5. Implement audit events, optimistic concurrency and rollback for mutation operations.
6. Replace the HTML prototype navigation completely with Blazor components after parity testing.
