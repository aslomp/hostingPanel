# Kubernetes website hosting

Enable the Kubernetes backend and set KubernetesHosting:Enabled to true in appsettings or environment variables. Install kubectl in the HostingPanel runtime and configure its kubeconfig or in-cluster credentials. Run only one active reconciliation worker per SQLite database.

Create the configured hosting namespace and grant the panel service account namespace-scoped permissions for ConfigMaps, Deployments, Services, HTTPRoutes and NetworkPolicies (get/list/create/patch/delete). Install Gateway API and Envoy Gateway separately. Configure GatewayName, GatewayNamespace, HttpListener and HttpsListener; the listener must permit HTTPRoutes from the hosting namespace. HTTPS certificates and listener provisioning are external prerequisites.

Set NfsServer and NfsPath to the NFS server and exported website root. Each website's directory must already exist at NfsPath/<domain GUID without hyphens>. The Apache deployment mounts only its directory, read-only, at /var/www/sites/<GUID>. PHP runtimes mount the entire export at /var/www/sites, writable, so FastCGI script paths match. Set appropriate NFS permissions. Website deletion intentionally preserves data.

Each site owns a ConfigMap, Apache Deployment, Service and HTTPRoute named site-<GUID>. A configuration hash triggers a rollout for only the affected site. Other vhosts are not regenerated. Reconciliation retries pending operations and does not claim that Kubernetes acceptance means the site is serving traffic.

Shared PHP-FPM deployments and ClusterIP services php-fpm-8 and optionally php-fpm-7 listen on TCP 9000. DefaultPhpVersion selects the runtime; SitePhpVersions can override it by hostname. PHP 7 requires EnablePhp7 and an explicitly configured maintained Php7Image. PHP 7 is end-of-life upstream. A NetworkPolicy limits FastCGI ingress to Apache pods in the namespace if the CNI enforces policies.

**Security boundary:** shared PHP runtimes are suitable only for mutually trusted applications. They can access the shared website tree with the same worker identity; NetworkPolicy does not isolate files or PHP scripts between tenants. Enable AllowSharedPhpRuntime only when this is acceptable. Untrusted multi-tenant hosting needs separately isolated PHP workers and storage permissions. Do not expose FastCGI publicly.

Changes to global configuration or per-domain PHP version overrides require a new website update operation to reconcile an existing website. This worker does not provision NFS directories, certificates, DNS or CGI and does not implement continuous drift detection. Validate images, permissions, Gateway acceptance and rollout readiness in a test cluster before production.
