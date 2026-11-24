# DNS provider capability

IHostingBackend.Dns exposes IDNSProvider independently of IWebHosting. The API delegates all DNS operations, including accessible inventory, to the selected backend. DirectAdmin aggregates its own DNS and account inventories; Kubernetes lists actual desired DNS zones, not all hosted domains.

The contract covers zone inventory, reading, creation and deletion, and record creation, update and deletion. The existing change operation is retained as the common primitive. Credentials and authorization remain provider-owned.

Kubernetes persists DNS-only zones (admin creation), or zones linked to accessible hosted domains, including initial A and NS records. Mutations return 202 and enqueue reconciliation work. No authoritative nameserver is configured by this implementation yet; a future BIND, PowerDNS or other implementation must reconcile the desired state and observe publication. DNS zones need not have a website. Existing HTTP routes and the pinned DirectAdmin Swagger remain unchanged; mutation responses now distinguish execution from pending provisioning.
