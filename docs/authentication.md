# Authentication and session lifetime

Applies to both backends. DirectAdmin authenticates against its upstream session; Kubernetes verifies the local unsuspended account and password. See [DirectAdmin](directadmin.md) for delegation and [configuration](configuration.md) for state ownership.

## Panel login

PanelAuthentication maps GET/POST /login and POST /logout separately from official /api/login compatibility routes. The login form uses antiforgery, bounded input validation and local-return validation. The login POST rate limit is 10 requests per minute per remote IP. Authentication invokes IAuthenticationProvider; rejection returns a login error and upstream availability errors are distinguished. Interactive Razor routes require cookie authorization; controller endpoints have their own backend authentication rules. Public health/specification endpoints are not an authenticated hosting operation.

Program.cs configures a secure HttpOnly SameSite=Lax cookie, without sliding expiration. Remember-me creates a persistent seven-day session; otherwise the server expiration is twelve hours. PanelSessionStore is an in-memory bounded cache of authenticated credentials, not persistent storage. The cookie contains a session reference; cookie validation checks that the server record still exists. Process restarts lose that record, so persistent browser cookies do not guarantee login survives a restart. Logout removes the record and signs out. Never write session credentials to logs, SQLite, UI payloads or browser storage.

PanelSession is circuit-scoped and restores the original identity from the session store. Effective credentials are captured for each operation; changing identity is separate from changing the navigation context. Delegated identity is authorized upstream in DirectAdmin; Kubernetes impersonation is not implemented. Dispose circuit-specific notification state independently from the browser session.

## Kubernetes local identity

KubernetesIdentityService authenticates Basic credentials against unsuspended local accounts. PasswordVerifier stores salted PBKDF2-SHA256 hashes and performs constant-time comparison. Its visibility query follows CreatorId descendants from the authenticated account; do not assume that admin role alone causes every record to be returned, or that visibility automatically grants every mutation. Account, database, DNS and ticket services have their own resource-specific authorization checks. Bootstrap creates an initial administrator only for an empty account database with explicit configured credentials; the default package is initialized if no packages exist.

No separate social-login implementation is established by this code review. No cross-replica session persistence is implemented. Do not equate the existence of official authentication forwarding routes with support for panel login methods.
