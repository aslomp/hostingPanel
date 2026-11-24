# DNS provider contract

The independent IDNSProvider capability owns accessible/admin zone inventory, zone read/create/delete and record changes. Controllers delegate to the selected backend. DirectAdmin is authoritative for its resources; Kubernetes stores desired DNS state in SQLite and reconciles to PowerDNS when configured. A 202 response denotes accepted desired state.

The complete current resolution, ownership and provisioning workflow is documented in [DNS](dns.md). Deployment details are in [PowerDNS](powerdns.md).
