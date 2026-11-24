# Kubernetes website hosting

Enable the Kubernetes backend and set KubernetesHosting:Enabled to true. Install kubectl in the HostingPanel runtime and configure kubeconfig or in-cluster credentials. Run one active reconciliation worker per SQLite database.

Create the hosting namespace. Grant the panel service account namespace-scoped permissions for ConfigMaps, Deployments, Services, HTTPRoutes and NetworkPolicies (get/list/create/patch/delete), and get/list pods. Install Gateway API and Envoy Gateway separately. Configure GatewayName, GatewayNamespace, HttpListener and HttpsListener; the listener must permit HTTPRoutes from the hosting namespace. HTTPS certificates are external prerequisites.

## Account identity and NFS

The provider-owned PhpAccountIdentities SQLite table allocates a stable UID, GID and TCP listener port per account from 10000–30000. Allocations are retained after account deletion to prevent reuse against retained NFS data. The same account uses the same identity on both PHP versions. Reserve this numeric range in the container images and on the NFS server.

Set NfsServer and NfsPath to a dedicated website export. Each domain directory must already exist at NfsPath/<domain GUID without hyphens>, owned by the account's allocated UID:GID with mode 0700. The export root must permit traversal, but must not contain unrelated data or publicly accessible tenant directories. The Apache startup checks its directory's ownership and mode. Provision directories and ownership on the NFS server, preserving root squash; the panel does not automatically provision them yet. Website deletion intentionally preserves data.

Apache mounts only its domain directory read-only; PHP-FPM mounts the complete export read-write. Both see identical absolute script paths under /var/www/sites/<domain GUID>. Apache and PHP workers run under the allocated account identity.

## Apache and PHP

Every site owns an Apache ConfigMap, Deployment, Service and HTTPRoute named site-<GUID>. Only the affected website is applied. A configuration hash triggers the corresponding Apache rollout. Kubernetes API acceptance does not imply the site is ready.

PHP-FPM has one Deployment, ConfigMap and ClusterIP Service per enabled major version (php-fpm-8, optionally php-fpm-7). Each account with a PHP-enabled domain has its own on-demand worker pool, Unix identity and TCP port. Multiple domains of the same account and PHP version share the pool. Php8Replicas and Php7Replicas configure replica counts; runtimes scale to zero with no configured pools. DefaultPhpVersion and SitePhpVersions select the version. PHP 7 is end-of-life upstream; it requires EnablePhp7 and an explicitly configured maintained Php7Image.

Account-specific NetworkPolicy ingress rules permit Apache pods to reach only their account's FastCGI port. PHP-FPM also restricts listen.allowed_clients to observed Apache IPv4 pod addresses; empty allowlists fail closed. PHP workers do not receive network capabilities or service-account credentials. Changes to account pools or Apache pod IPs may trigger a rollout of the corresponding shared PHP runtime, but do not regenerate other Apache configurations.

## Security and validation

AllowSharedPhpRuntime remains explicit opt-in. Shared processes and a shared network namespace are not a hard tenant boundary. The source-IP approach requires a CNI enforcing NetworkPolicy, preservation of client pod IPs, trusted namespace labels and careful handling of stale/reused IPs during rollouts. NFS permissions are essential and must be enforced for all directories, not just the directory inspected by Apache. Do not expose FastCGI publicly. Validate cross-account file and FastCGI access, including from inside a PHP worker, before allowing untrusted tenants. Use separately isolated PHP workloads for tenants requiring a stronger boundary.

Global configuration changes and per-domain PHP version changes require a website update to reconcile its Apache configuration. The runtime pool manifest is checked periodically. DNS, certificates, CGI, NFS directory creation and general Kubernetes drift recovery are not implemented here. Build and cluster validation are required before production.
