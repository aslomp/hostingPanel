# Web hosting capability

IWebHosting is an independent capability for the logical website of a hosted domain. The two providers share domain lifecycle semantics rather than creating a second website inventory. DirectAdmin uses its existing domain operations; Kubernetes persists intent and schedules website reconciliation.

| Operation | DirectAdmin | Kubernetes |
| --- | --- | --- |
| List/get | Upstream inventory/config | SQLite desired state and operation status |
| Create/update/delete | Upstream domain operations | Desired state and asynchronous reconciliation |
| Runtime readiness | Requires observation | Requires observation of configuration, routes and serving workloads |

HTTP routes: GET /hosting/websites (optional owner), GET /hosting/websites/{domain}, POST /hosting/websites, PUT /hosting/websites, DELETE /hosting/websites/{domain}. Delete requires exact domain confirmation. Verify request DTOs and supported flags in the current contract before extending them.

Kubernetes uses one shared Apache service and per-domain vhost ConfigMaps/HTTPRoutes, not separate Apache deployments. Domain lifecycle also stages DNS where enabled and reconciles certificates and cleanup. Do not confuse website acceptance with a ready URL. See [Kubernetes hosting](kubernetes-hosting.md) and [DNS](dns.md).
