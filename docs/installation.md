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
