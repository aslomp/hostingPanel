# PowerDNS authoritative provisioning

The Kubernetes backend retains SQLite desired state and authorization. The reusable PowerDNS library translates desired RRsets into the authoritative server API. Pending dns-zone operations are retried independently of website provisioning. PowerDNS owns SOA records. Existing operational zones with matching names are assumed managed by HostingPanel; use a dedicated server/database, not a shared existing DNS installation.

Helm installs one PowerDNS instance and a separate persistent SQLite volume when backend=Kubernetes and powerDns.enabled=true. DNS uses TCP and UDP port 53; the management API has an internal service and network policy. Supply an existingSecret containing api-key if preferred. The default service type is LoadBalancer; choose ClusterIP for private testing. Configure public nameserver delegation separately. Zone creation remains explicit through the existing DNS contract. AXFR is disabled until secondary servers are configured.

For external PowerDNS set PowerDns:Enabled, PowerDns:BaseUrl, PowerDns:ApiKey and optionally PowerDns:ServerId. The provider requires exclusive ownership of managed RRsets. Changes performed directly in PowerDNS are not authoritative desired state.
