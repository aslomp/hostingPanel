# Web hosting capability

IHostingBackend.WebHosting exposes IWebHosting. A website is the logical web-serving configuration for a domain, not an Apache virtual host, NGINX server block or Kubernetes object. The current providers use one website per hosted domain and share the existing domain lifecycle, avoiding duplicate records. Domain registration at a registrar is not implemented here.

| Operation | DirectAdmin | Kubernetes |
|---|---|---|
| Create/update/delete | Existing DirectAdmin domain lifecycle calls, authorized by DirectAdmin | SQLite desired state + website reconciliation outbox |
| List/get | Existing domain inventory and configuration | SQLite inventory/configuration; pending operations reported |
| Runtime readiness | Unknown until observed | Unknown or pending; no running reconciler yet |

The Kubernetes backend records website intent with the domain ID, owner ID, desired configuration and selected ingress provider. Existing domain/account creation routes also enqueue that intent. A future reconciler should resolve current effective backend settings, reconcile the web workload/service and routing resources, observe readiness and remove only provider-owned resources on deletion. The outbox payload is not evidence that a cluster resource exists. Treat old queued operations as signals to reconcile current desired state rather than blindly replaying outdated configuration. Actual Apache/NGINX virtual host generation, Kubernetes workload creation and routing reconciliation are not implemented by this change.

## HTTP API

- GET /hosting/websites?owner=optional
- GET /hosting/websites/{domain}
- POST /hosting/websites
- PUT /hosting/websites/{domain}
- DELETE /hosting/websites/{domain} with JSON confirmation equal to domain

Create/update request: {"domain":"example.com","php":true,"tls":true,"cgi":false}. Update replaces these three settings. Current providers retain existing domain lifecycle semantics, including deletion restrictions and DirectAdmin's coupled domain deletion behavior. No registrar, DNS or certificate provisioning is implied. Authorization stays in the provider. 200 indicates provider-confirmed configuration, 202 accepted desired state, not HTTP availability. The official DirectAdmin Swagger remains unchanged.

New providers implement IWebHosting independently, with server-specific configuration in BackendSettings. The API has no dependency on the chosen server technology.
