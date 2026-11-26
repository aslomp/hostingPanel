# Installation (unified host)

Requires .NET 10 SDK for source builds, Docker Compose v2 for containers, or Helm and a Kubernetes cluster for chart deployment. HostingPanel.Web is the only application image; there is no separate API service.

## Local DirectAdmin
```sh
export HostingPanel__Backend=DirectAdmin
export DirectAdmin__BaseUrl=https://your-directadmin.example:2222/
dotnet dev-certs https --trust
dotnet run --project src/HostingPanel.Web --urls https://localhost:7201
```
Open https://localhost:7201. Use your own account credentials. Keep upstream certificate validation enabled.

## Docker Compose
```sh
cd deploy/compose
cp .env.example .env
chmod 600 .env
docker compose config --quiet
docker compose up -d --build
docker compose ps
docker compose logs --tail=100 web
```
Set HOSTING_BACKEND and the appropriate DirectAdmin origin or local bootstrap values in the protected .env. Compose binds the web service to loopback by default; use HTTPS and a WebSocket-capable reverse proxy for normal browser access. SQLite persists in panel-data. The Compose profile does not itself supply Kubernetes hosting infrastructure.

## Helm
Build the single image:
```sh
docker build -t registry.example.invalid/hostingpanel:YOUR_TAG .
docker push registry.example.invalid/hostingpanel:YOUR_TAG
```
Use a local values file selecting backend and web.image.repository/tag. For DirectAdmin set directAdmin.baseUrl. For Kubernetes set bootstrap.existingSecret to an existing secret with username/password, then remove the bootstrap reference when initialization is complete. Use an appropriate SQLite PVC with reliable locking and a single panel replica. Do not commit credentials.

```sh
helm template panel deploy/helm/hostingpanel -n hostingpanel -f local-values.yaml
helm upgrade --install panel deploy/helm/hostingpanel -n hostingpanel --create-namespace -f local-values.yaml
kubectl -n hostingpanel get pods,svc,pvc
```

For actual hosting see [Kubernetes hosting](kubernetes-hosting.md), [DNS](dns.md), [configuration](configuration.md) and the current chart. Configure trusted HTTPS ingress. Verify login, /healthz and the requested backend operations independently. A healthy panel does not establish that websites, DNS or certificates are ready.

## Dedicated Apache configuration export

For enabled Kubernetes hosting, create a separate NFS export on the configured
nfsServer and set kubernetesHosting.apacheConfigPath to its absolute server-side
path. It must not overlap nfsPath or be accessible from customer workloads.
The chart mounts it directly, writable in the panel (UID/GID 1654) and read-only
in Apache (UID/GID 1001). Configure export permissions on the server so that
the panel can create revision directories and files and Apache can read them.
Keep root squash and restrict NFS access to authorized nodes. Verify the actual
permissions and mounts; Kubernetes fsGroup alone does not configure NFS ownership.

No Apache PV, PVC, StorageClass or CSI driver is required. When upgrading from
apacheConfigClaim, set apacheConfigPath instead and point it at the same dedicated
export if appropriate. Do not delete the previous claim or its data automatically.
The backend regenerates configuration from desired state if the export is empty.

## MariaDB and iSCSI alongside NFS

NFS remains the shared website filesystem. MariaDB can use an independent
block-backed filesystem PVC. Install the storage vendor's supported CSI driver
with iSCSI support separately; Kubernetes does not include a generic dynamic
iSCSI provisioner. Configure an iSCSI target/storage pool on the storage system,
restrict access to cluster nodes, and configure the CSI driver's credentials and
StorageClass according to its documentation. On every eligible Linux node,
install and enable the initiator required by that driver (for Debian/Ubuntu,
typically `sudo apt install open-iscsi` and `sudo systemctl enable --now iscsid`).
Ensure nodes can reach the target (normally TCP 3260), use unique initiator IQNs,
and configure CHAP and multipath where required by the storage vendor. Keep
storage traffic on a restricted network. Do not expose the target publicly.

Check the actual class name with `kubectl get storageclass`. Test a temporary
ReadWriteOnce filesystem PVC with that class and a consumer pod before deploying
MariaDB; verify attach, mount, writes and persistence after pod recreation.
Delete only the test resources afterward. StorageClass parameters are
driver-specific, not HostingPanel settings.

Create a Secret named `hostingpanel-mariadb` in the release namespace with a
strong randomly generated `root-password` key using your secret manager or
Kubernetes Secret tooling. Do not commit the password or put it in Helm values.

Merge these settings into the existing Kubernetes hosting values (replace
the illustrative class and NFS endpoints with actual infrastructure):

```yaml
backend: Kubernetes
kubernetesHosting:
  enabled: true
  nfsServer: nfs.example.internal
  nfsPath: /home
  apacheConfigPath: /exports/apache-config
mariadb:
  enabled: true
  storageClassName: iscsi
  size: 20Gi
  existingSecret: hostingpanel-mariadb
```

Keep the other Gateway, image, bootstrap and DNS settings from the hosting
profile. The chart creates a single MariaDB StatefulSet and a ClusterIP service
named `<release>-mariadb` on port 3306, plus a ReadWriteOnce filesystem PVC
unless `mariadb.existingClaim` points to a compatible pre-existing claim in
the release namespace. An empty storageClassName uses the cluster default.
The generated PVC is retained on Helm uninstall; back up data separately and
verify the storage system's reclaim policy. Do not change an existing PVC's
storage class in place: migrate using a verified backup/restore or supported
volume migration. Never mount one MariaDB data directory into multiple servers.

After deployment check `kubectl -n hosting get statefulsets,pods,pvc,svc`
(adjust namespace), confirm the claim is Bound to the intended class, and
verify MariaDB readiness and authenticated database access. Use separately
created least-privilege application accounts; never give hosted PHP applications
the root password. MariaDB initialization credentials apply to an empty data
directory; changing the Secret alone does not rotate an existing database password.

This deploys database infrastructure only. HostingPanel Kubernetes database
create/delete operations remain unsupported; external/manual database
administration is required until that capability is implemented.
