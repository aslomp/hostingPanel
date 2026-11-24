# Kubernetes website hosting

Enable the Kubernetes backend and set KubernetesHosting:Enabled to true. Install kubectl in the HostingPanel runtime and configure kubeconfig or in-cluster credentials. Run one active reconciliation worker per SQLite database.

Create the hosting namespace. Grant the panel service account namespace-scoped permissions for ConfigMaps, Deployments, Services, HTTPRoutes and NetworkPolicies (get/list/create/patch/delete), and get/list pods. Install Gateway API and Envoy Gateway separately. Configure GatewayName, GatewayNamespace, HttpListener and HttpsListener; the listener must permit HTTPRoutes from the hosting namespace. HTTPS certificates are external prerequisites.

## Account identity and NFS

The provider-owned PhpAccountIdentities SQLite table allocates a stable UID, GID and TCP listener port per account from 10000–30000. Allocations are retained after account deletion to prevent reuse against retained NFS data. The same account uses the same identity on both PHP versions. Reserve this numeric range in the container images and on the NFS server.

Set NfsServer and NfsPath to a dedicated website export. Each domain directory is provisioned automatically at NfsPath/<domain GUID without hyphens>, owned by the account's allocated UID:GID with mode 0700. The export root must permit traversal, but must not contain unrelated data or publicly accessible tenant directories. The Apache startup checks its directory's ownership and mode. Set NfsProvisioningPath to the trusted storage-side filesystem path corresponding exactly to NfsPath. The provisioner needs permission to create directories and assign the allocated ownership there. A normal root-squashed client mount cannot perform arbitrary chown; use an authorized storage-side provisioning environment, not a privileged web-serving pod or a weakened NFS export. The parent and its ancestors must be administrator-controlled, with no tenant write access. Existing directories with unexpected ownership or permissions are rejected, never repaired recursively. Website deletion intentionally preserves data.

Apache mounts only its domain directory read-only; PHP-FPM mounts the complete export read-write. Both see identical absolute script paths under /var/www/sites/<domain GUID>. Apache and PHP workers run under the allocated account identity.

## Apache and PHP

Every site owns an Apache ConfigMap, Deployment, Service and HTTPRoute named site-<GUID>. Only the affected website is applied. A configuration hash triggers the corresponding Apache rollout. Kubernetes API acceptance does not imply the site is ready.

PHP-FPM has one Deployment, ConfigMap and ClusterIP Service per enabled major version (php-fpm-8, optionally php-fpm-7). Each account with a PHP-enabled domain has its own on-demand worker pool, Unix identity and TCP port. Multiple domains of the same account and PHP version share the pool. Php8Replicas and Php7Replicas configure replica counts; runtimes stay running with a loopback-only, deny-all idle pool when no account pools exist. DefaultPhpVersion and SitePhpVersions select the version. PHP 7 is end-of-life upstream; it requires EnablePhp7 and an explicitly configured maintained Php7Image.

Account-specific NetworkPolicy ingress rules permit Apache pods to reach only their account's FastCGI port. PHP-FPM also restricts listen.allowed_clients to observed Apache IPv4 pod addresses; empty allowlists fail closed. PHP workers do not receive network capabilities or service-account credentials. Changes to account pools or Apache pod IPs may trigger a rollout of the corresponding shared PHP runtime, but do not regenerate other Apache configurations.

## Security and validation

AllowSharedPhpRuntime remains explicit opt-in. Shared processes and a shared network namespace are not a hard tenant boundary. The source-IP approach requires a CNI enforcing NetworkPolicy, preservation of client pod IPs, trusted namespace labels and careful handling of stale/reused IPs during rollouts. NFS permissions are essential and must be enforced for all directories, not just the directory inspected by Apache. Do not expose FastCGI publicly. Validate cross-account file and FastCGI access, including from inside a PHP worker, before allowing untrusted tenants. Use separately isolated PHP workloads for tenants requiring a stronger boundary.

Global configuration changes and per-domain PHP version changes require a website update to reconcile its Apache configuration. The runtime pool manifest is checked periodically. DNS, certificates, CGI and general Kubernetes drift recovery are not implemented here. Build and cluster validation are required before production.

## Installing in namespace hosting

Use the Harbor installation profile `deploy/helm/values-hosting.example.yaml`. It selects the Kubernetes backend, `harbor.eagleconnect.nl/hosting/admin:latest`, Apache 2.4 and PHP 8.6.0 FPM only. It creates a SQLite PVC, a single admin replica, a namespaced service account and RBAC, an internal baseline Apache service, and optionally an Envoy HTTPRoute for the admin. The application reconciles the shared PHP deployment and service and creates real Apache website deployments on demand; the baseline has no external route.

Before installation, rebuild and publish the admin image from this revision, including the idle FPM change. The chart downloads a checksum-verified kubectl in an init container; outbound HTTPS to dl.k8s.io is required. Select a kubectl minor compatible with the cluster.

Create the namespace and bootstrap secret (using a strong password supplied interactively):

```bash
kubectl create namespace hosting --dry-run=client -o yaml | kubectl apply -f -
read -rs -p 'Bootstrap password: ' BOOTSTRAP_PASSWORD; echo
printf '%s' "$BOOTSTRAP_PASSWORD" | kubectl -n hosting create secret generic hostingpanel-bootstrap --from-literal=username=admin --from-file=password=/dev/stdin
unset BOOTSTRAP_PASSWORD
```

Copy the profile to a local values file and fill in nfsServer, nfsPath, gatewayName, gatewayNamespace and the actual listener names. Configure an existing registry pull secret if necessary. Use a dedicated namespace: the controller can manage workloads there and should not share a namespace with unrelated secrets or workloads. Shared PHP is enabled in this example only for trusted sites.

```bash
helm upgrade --install admin deploy/helm/hostingpanel \
  --namespace hosting --create-namespace -f hosting-values.yaml
kubectl -n hosting get deployments,services,pods
```

By default the admin mounts the website export read-only for validation of existing directories. The unprivileged admin cannot assign arbitrary UID/GID on a root-squashed NFS client. Automatic creation requires an authorized storage-side provisioning arrangement that presents the same filesystem and permits the required ownership changes, or pre-provisioning directories on the NFS server. An existing provisioningClaim can present such a filesystem but does not itself grant ownership-changing authority. Do not run the admin as root or disable root squash to work around this. The admin remains accessible internally and can be tested with `kubectl -n hosting port-forward svc/admin-hostingpanel-web 8080:8080` (secure cookies require HTTPS for normal login).
