install_platform() {
  if ! command -v helm >/dev/null; then
    local tmp arch version archive
    tmp=$(mktemp -d)
    case $(uname -m) in x86_64) arch=amd64;; aarch64) arch=arm64;; *) die "Unsupported architecture";; esac
    version=$(require helmVersion); archive="helm-$version-linux-$arch.tar.gz"
    curl -fsSL "https://get.helm.sh/$archive" -o "$tmp/$archive"
    curl -fsSL "https://get.helm.sh/$archive.sha256sum" -o "$tmp/$archive.sha256sum"
    (cd "$tmp"; printf "%s  %s\n" "$(awk 'NR==1 {print $1}' "$archive.sha256sum")" "$archive" | sha256sum -c -)
    tar -xzf "$tmp/$archive" -C "$tmp"
    install -m 0755 "$tmp/linux-$arch/helm" /usr/local/bin/helm
    rm -rf "$tmp"
  fi
  [[ "$(helm version --template '{{.Version}}')" == "$(require helmVersion)" ]] || die "Helm version mismatch"
  local base
  base=$(dirname "$config")
  python3 "$here/generate.py" "$config" generate
  helm repo add democratic-csi https://democratic-csi.github.io/charts/ --force-update
  helm repo add jetstack https://charts.jetstack.io --force-update
  helm repo update
  for protocol in nfs iscsi; do
    local values="$base/csi-$protocol.json"
    [[ -s "$values" ]] || die "Missing $values (driver SSH and ZFS configuration)"
    helm upgrade --install "zfs-$protocol" democratic-csi/democratic-csi -n democratic-csi --create-namespace --version "$(require csiVersion)" -f "$values" --wait --timeout 10m
  done
  helm upgrade --install eg oci://docker.io/envoyproxy/gateway-helm -n envoy-gateway-system --create-namespace --version "$(require envoyVersion)" --wait --timeout 10m
  helm upgrade --install cert-manager jetstack/cert-manager -n cert-manager --create-namespace --version "$(require certManagerVersion)" --set crds.enabled=true --set config.enableGatewayAPI=true --wait --timeout 10m
  k3s kubectl wait --for=condition=Established crd/listenersets.gateway.networking.k8s.io --timeout=120s
  k3s kubectl apply -f "$base/issuer.json"
  k3s kubectl apply -f "$base/gateways.json"
  if [[ -n $(get panelHostname) ]]; then
    ensure_panel_tls "$base"
  fi
  # ACME account readiness is not an installation prerequisite.
  if ! k3s kubectl wait --for=condition=Ready clusterissuer/lets-encrypt --timeout=15s; then
    echo "ACME issuer not ready; initial panel TLS does not depend on ACME" >&2
  fi
  bash "$here/lib/storage-smoke.sh"
  k3s kubectl create namespace hosting --dry-run=client -o yaml | k3s kubectl apply -f -
  ensure_secret hostingpanel-bootstrap username admin password
  ensure_secret hostingpanel-mariadb root-password
  ensure_secret hostingpanel-mariadb-provisioning password
  [[ -s "$base/hosting-values.json" ]] || die "Missing hosting-values.json"
  if [[ $(get buildPanelImage) == True ]]; then
    apt-get install -y docker.io
    systemctl start docker
    local image="$(require panelImage):$(require panelTag)"
    docker build -t "$image" "$here/.."
    docker save "$image" | k3s ctr images import -
  fi
  helm template admin "$here/../deploy/helm/hostingpanel" -n hosting -f "$base/hosting-values.json" >/dev/null
  helm upgrade --install admin "$here/../deploy/helm/hostingpanel" -n hosting -f "$base/hosting-values.json" --wait --timeout 10m
  if [[ -z $(get panelHostname) ]]; then
    k3s kubectl -n envoy-gateway-system wait --for=condition=Programmed gateway/external --timeout=300s
    local pod
    pod=$(k3s kubectl -n hosting get pods -l app.kubernetes.io/instance=admin,app.kubernetes.io/component=web -o jsonpath='{.items[0].metadata.name}')
    [[ -n "$pod" ]] || die "Panel pod missing"
    k3s kubectl -n hosting wait --for=condition=Ready "pod/$pod" --timeout=300s
    echo "Panel is private until a public hostname is configured. Use kubectl port-forward over SSH."
    return
  fi
  k3s kubectl -n envoy-gateway-system wait --for=condition=Ready certificate/hosting-panel-tls --timeout=600s
  k3s kubectl -n envoy-gateway-system wait --for=condition=Programmed gateway/external --timeout=300s
  check_panel_https
}

ensure_secret() (
  local name=$1
  shift
  local existing
  existing=$(k3s kubectl -n hosting get secret "$name" --ignore-not-found -o name)
  if [[ -n "$existing" ]]; then return; fi
  local tmp
  tmp=$(mktemp -d)
  trap 'rm -rf -- "$tmp"' EXIT
  if [[ "$name" == hostingpanel-bootstrap ]]; then
    printf admin > "$tmp/username"
    openssl rand -hex 32 | tr -d '\n' > "$tmp/password"
    k3s kubectl -n hosting create secret generic "$name" --from-file=username="$tmp/username" --from-file=password="$tmp/password"
  else
    openssl rand -hex 32 | tr -d '\n' > "$tmp/$1"
    k3s kubectl -n hosting create secret generic "$name" --from-file="$1=$tmp/$1"
  fi
)

# Keep a previously issued certificate, including a manually promoted ACME certificate.
ensure_panel_tls() {
  local base=$1 ns=envoy-gateway-system existing secret
  existing=$(k3s kubectl -n "$ns" get certificate hosting-panel-tls --ignore-not-found -o name)
  secret=$(k3s kubectl -n "$ns" get secret hosting-panel-tls --ignore-not-found -o name)
  # Remove legacy shim ownership of issuance intent (not the Secret).
  k3s kubectl -n "$ns" annotate gateway external cert-manager.io/cluster-issuer- --overwrite
  if [[ -z "$existing" || -z "$secret" ]]; then
    k3s kubectl apply -f "$base/panel-bootstrap-tls.json"
    k3s kubectl -n "$ns" wait --for=condition=Ready certificate/hosting-panel-tls --timeout=180s
  fi
}

check_panel_https() (
  local tmp hostname
  tmp=$(mktemp -d)
  trap 'rm -rf -- "$tmp"' EXIT
  hostname=$(require panelHostname)
  k3s kubectl -n envoy-gateway-system get secret hosting-panel-tls -o jsonpath='{.data.tls\.crt}' | base64 -d > "$tmp/cert.pem"
  openssl x509 -in "$tmp/cert.pem" -noout -checkhost "$hostname"
  openssl x509 -in "$tmp/cert.pem" -noout -checkend 0
  openssl x509 -in "$tmp/cert.pem" -pubkey -noout > "$tmp/pubkey.pem"
  # Trust the key obtained through the authenticated Kubernetes API, not public DNS.
  curl --fail --silent --show-error --retry 5 --retry-all-errors --connect-timeout 10 --max-time 30 \
    --noproxy '*' --resolve "$hostname:443:$(require webIp)" --insecure --pinnedpubkey "$tmp/pubkey.pem" "https://$hostname/healthz"
  echo "Panel HTTPS ready. Bootstrap certificates are not publicly trusted; configure ACME when public DNS and ingress are ready."
)
