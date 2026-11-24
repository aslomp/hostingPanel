# Installing HostingPanel

HostingPanel has two independently selectable dimensions: deployment (Docker Compose, Kubernetes/Helm or native .NET) and provider (DirectAdmin or experimental Kubernetes). The Blazor Server frontend calls the API on the server side; browsers do not need direct API access.

| Provider | Credentials | What works today |
|---|---|---|
| DirectAdmin (default) | Existing DirectAdmin username and login key/password | Existing supported hosting operations, forwarded to the configured HTTPS DirectAdmin server |
| Kubernetes (experimental) | Explicitly bootstrapped local administrator | SQLite control-plane API for supported operations; no actual workload reconciliation; modern session endpoint is not yet implemented, so UI sign-in is unavailable |

Do not expose the experimental provider publicly or treat a successful desired-state mutation as proof of provisioning.

## 1. Docker Compose — quickest installation

Prerequisites: Git, Docker Engine or Docker Desktop with the Compose v2 plugin, outbound access to the Microsoft container registry and NuGet. Images are built from source; no published HostingPanel registry images are assumed.

```bash
git clone https://github.com/aslomp/hostingPanel.git
cd hostingPanel/deploy/compose
cp .env.example .env
chmod 600 .env
```

Edit `.env` with your real DirectAdmin HTTPS origin (including port and trailing slash). Keep `HOSTING_BACKEND=DirectAdmin`. Do not put a username or password in the origin. The API container must trust the DirectAdmin server's TLS certificate; for private CAs build an image that installs your organization's CA, never disable validation.

```bash
docker compose config --quiet
docker compose up -d --build
docker compose ps
docker compose logs --tail=100 api web
```

Open **http://127.0.0.1:8080** on the Docker host. Enter your DirectAdmin username and login key/password in the Blazor form. The API is not published to the host. This HTTP address is for loopback access only. For remote access use an HTTPS reverse proxy supporting WebSockets (Blazor/SignalR), or an SSH tunnel to the loopback port. For cross-host traffic protect the API transport with TLS as well.

### Experimental SQLite provider

Set `HOSTING_BACKEND=Kubernetes` in `.env`. Set `BOOTSTRAP_USERNAME` to your chosen administrator and `BOOTSTRAP_PASSWORD` to a unique password of at least 12 characters (prefer 24+). A newly created empty database requires both settings for this Compose template. Restart with `docker compose up -d --build`. The SQLite volume persists when containers are recreated. After first bootstrap, clear the two bootstrap values from `.env` and recreate the API container. Keep the administrator password in a password manager.

The UI cannot yet authenticate to this provider. Use the API verification procedure in section 4. No real websites, databases or DNS services are provisioned.

## 2. Kubernetes / K3s with Helm

Prerequisites: Kubernetes cluster, kubectl, Helm 3+, Docker or compatible builder, and a registry reachable by cluster nodes. SQLite mode additionally needs a persistent volume supporting POSIX file locks and SQLite WAL. Prefer local block-backed storage; never use a shared network filesystem with unreliable locking. Use a single API replica and Recreate strategy. The chart intentionally grants no Kubernetes API permissions and mounts no service account token.

Build and publish two images using the repository root as build context. Replace the registry and tag with your own, immutable version. Authenticate to your registry as appropriate.

```bash
cd hostingPanel
export REGISTRY=registry.example.invalid/hostingpanel
export TAG="$(git rev-parse --short HEAD)"
docker build --target api -t "$REGISTRY/api:$TAG" .
docker build --target web -t "$REGISTRY/web:$TAG" .
docker push "$REGISTRY/api:$TAG"
docker push "$REGISTRY/web:$TAG"
kubectl create namespace hostingpanel
```

For a private registry, create a namespace-scoped registry pull secret using your approved secret-management workflow and reference it in `imagePullSecrets` in a local values override. Do not commit registry credentials.

### DirectAdmin provider on Kubernetes

```bash
helm upgrade --install panel deploy/helm/hostingpanel \
  --namespace hostingpanel \
  --set backend=DirectAdmin \
  --set-string directAdmin.baseUrl=https://da.example.invalid:2222/ \
  --set-string api.image.repository="$REGISTRY/api" \
  --set-string api.image.tag="$TAG" \
  --set-string web.image.repository="$REGISTRY/web" \
  --set-string web.image.tag="$TAG" \
  --wait --timeout 5m
```

### Experimental Kubernetes provider

Create a secret named `hostingpanel-bootstrap` in namespace `hostingpanel` with keys `username` and `password` using your secret-management workflow. For local testing, create temporary files with permissions 0600 containing the values (no trailing newline); then use:

```bash
kubectl -n hostingpanel create secret generic hostingpanel-bootstrap \
  --from-file=username=/path/to/protected/username \
  --from-file=password=/path/to/protected/password
```

Delete the temporary files securely according to your local policy. Never use real credentials in example manifests, command arguments, shell history or committed values files. Kubernetes Secrets need appropriate RBAC and encryption at rest.

```bash
helm upgrade --install panel deploy/helm/hostingpanel \
  --namespace hostingpanel \
  --set backend=Kubernetes \
  --set bootstrap.existingSecret=hostingpanel-bootstrap \
  --set-string api.image.repository="$REGISTRY/api" \
  --set-string api.image.tag="$TAG" \
  --set-string web.image.repository="$REGISTRY/web" \
  --set-string web.image.tag="$TAG" \
  --wait --timeout 5m
```

The chart uses the default StorageClass unless `sqlite.storageClassName` is set. For K3s with local-path storage, consider node affinity and node-loss recovery; a local volume is not a backup. To reuse an existing claim set `sqlite.existingClaim`. The storage driver must support the configured non-root ownership (UID/GID 1654). Do not fix permissions by making the volume world-writable.

After confirming the administrator exists, run the same Helm upgrade with `--set bootstrap.existingSecret=` and the same remaining values. Verify the rollout, then remove the bootstrap Secret if it is no longer needed.

### Access and checks

```bash
kubectl -n hostingpanel get pods,svc,pvc
kubectl -n hostingpanel logs deploy/panel-hostingpanel-api
kubectl -n hostingpanel port-forward svc/panel-hostingpanel-web 8080:8080
```

Open http://localhost:8080. This is a local tunnel, not a public endpoint. The API is a ClusterIP service and an ingress NetworkPolicy restricts access to the web pods (requires a CNI that enforces NetworkPolicy). The policy does not encrypt intra-cluster traffic.

For HTTPS access, use an existing reverse proxy/Gateway supporting WebSockets. The optional [HTTPRoute template](../deploy/gateway/httproute.yaml.example) is for an existing Gateway API HTTPS listener, including Envoy Gateway; edit its parent reference, hostname and namespace before applying. Alternatively enable the chart's Ingress and set `ingress.className`, `ingress.host` and `ingress.tlsSecret` to match your existing ingress controller and certificate. Do not enable both. Restrict administrative access to trusted networks. For a public deployment, configure and validate trusted forwarded headers, HTTPS redirection, WebSockets, and end-to-end TLS as appropriate; these templates do not configure a TLS infrastructure or expose the API externally.

## 3. Native .NET 10

Prerequisites: .NET 10 SDK and Git. For local development, trust the development HTTPS certificate with `dotnet dev-certs https --trust` on supported platforms; otherwise use a trusted certificate configured in Kestrel. Do not ignore TLS errors.

From the repository root, in separate terminals:

```bash
export DirectAdmin__BaseUrl=https://da.example.invalid:2222/
export HostingPanel__Backend=DirectAdmin
dotnet run --project src/HostingPanel.Api --urls https://localhost:7101
```

```bash
export HostingPanel__ApiBaseUrl=https://localhost:7101/
dotnet run --project src/HostingPanel.Web --urls https://localhost:7201
```

Open https://localhost:7201. Swagger is available locally at https://localhost:7101/swagger. To test SQLite, set the backend, database path and bootstrap credentials in the API process environment instead; see [backend guide](kubernetes-sqlite-backend.md). Use a protected data directory owned by the running user.

## 4. Verify the API and first SQLite account

For Helm, in a separate terminal:
```bash
kubectl -n hostingpanel port-forward svc/panel-hostingpanel-api 7101:8080
```
Use `http://127.0.0.1:7101` over this local port-forward. For native HTTPS use `https://localhost:7101`. For Compose, run checks inside the deployment network using an approved temporary diagnostic container, or use the web UI for DirectAdmin; the API deliberately has no published port.

```bash
curl --fail http://127.0.0.1:7101/healthz
curl --fail --user admin http://127.0.0.1:7101/hosting/accounts
```

Replace admin with the bootstrap username. curl prompts for the password rather than placing it in shell history. Expect HTTP 200 and an account list containing that account. Incorrect credentials must return HTTP 401. In Kubernetes mode `/api/session` returns 501 until that operation is implemented. DirectAdmin mode requires the upstream server to support the session endpoint.

## 5. Upgrades, backup and removal

Pin image tags, record the exact source commit, and back up SQLite before every upgrade. The current schema is initialized with EnsureCreated, not versioned migrations. There is no supported automatic schema upgrade yet; verify compatibility on a restored copy before upgrading. Stop the API before taking an offline copy of the database directory, including any SQLite sidecar files; alternatively use SQLite's supported online backup API. Encrypt and restrict access to backups. Validate restorations in an isolated environment.

For Compose, `docker compose down` stops services while keeping the named volume. Do not run `docker compose down -v` unless you intentionally want to destroy panel data. For Helm, `helm uninstall panel -n hostingpanel` retains the chart-created SQLite claim through its keep annotation; inspect the claim and storage reclaim policy before any manual deletion. Reuse the existing claim explicitly when reinstalling. Deleting the namespace can delete the claim regardless of Helm's annotation.

### Troubleshooting

| Symptom | Check |
|---|---|
| ImagePullBackOff | Registry URL, immutable tag, credentials and node connectivity |
| API pod not ready | API logs, SQLite volume ownership, bootstrap settings |
| PVC Pending | StorageClass, provisioner and node capacity |
| DirectAdmin 502 | DNS, network connectivity and upstream TLS trust |
| UI session returns 501 | Experimental Kubernetes provider does not implement the session API yet |
| UI disconnects behind proxy | WebSocket upgrade, proxy timeout, trusted forwarded headers and TLS |
| SQLite locked | Ensure exactly one API instance uses the volume |

See [architecture](architecture.md) and [Kubernetes provider status](kubernetes-sqlite-backend.md) for current implementation boundaries.
