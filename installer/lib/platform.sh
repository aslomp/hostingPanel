install_platform() {
  if ! command -v helm >/dev/null; then
    local tmp arch version archive
    tmp=$(mktemp -d)
    case $(uname -m) in x86_64) arch=amd64;; aarch64) arch=arm64;; *) die "Unsupported architecture";; esac
    version=$(require helmVersion); archive="helm-$version-linux-$arch.tar.gz"
    curl -fsSL "https://get.helm.sh/$archive" -o "$tmp/$archive"
    curl -fsSL "https://get.helm.sh/$archive.sha256sum" -o "$tmp/$archive.sha256sum"
    (cd "$tmp"; sha256sum -c "$archive.sha256sum")
    tar -xzf "$tmp/$archive" -C "$tmp"
    install -m 0755 "$tmp/linux-$arch/helm" /usr/local/bin/helm
    rm -rf "$tmp"
  fi
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
  k3s kubectl apply -f "$base/gateways.json"
  k3s kubectl apply -f "$base/issuer.json"
  k3s kubectl wait --for=condition=Ready clusterissuer/lets-encrypt --timeout=180s
  k3s kubectl create namespace hosting --dry-run=client -o yaml | k3s kubectl apply -f -
  ensure_secret hostingpanel-bootstrap username admin password
  ensure_secret hostingpanel-mariadb root-password
  ensure_secret hostingpanel-mariadb-provisioning password
  [[ -s "$base/hosting-values.json" ]] || die "Missing hosting-values.json"
  helm template admin "$here/../deploy/helm/hostingpanel" -n hosting -f "$base/hosting-values.json" >/dev/null
  helm upgrade --install admin "$here/../deploy/helm/hostingpanel" -n hosting -f "$base/hosting-values.json" --wait --timeout 10m
}

ensure_secret() {
  local name=$1
  shift
  if k3s kubectl -n hosting get secret "$name" >/dev/null 2>&1; then return; fi
  local tmp
  tmp=$(mktemp -d)
  if [[ "$name" == hostingpanel-bootstrap ]]; then
    printf admin > "$tmp/username"
    openssl rand -hex 32 | tr -d '\n' > "$tmp/password"
    k3s kubectl -n hosting create secret generic "$name" --from-file=username="$tmp/username" --from-file=password="$tmp/password"
  else
    openssl rand -hex 32 | tr -d '\n' > "$tmp/$1"
    k3s kubectl -n hosting create secret generic "$name" --from-file="$1=$tmp/$1"
  fi
  rm -rf "$tmp"
}
