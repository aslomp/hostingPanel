install_platform() {
  command -v helm >/dev/null || die "Install Helm before running full-server"
  local base
  base=$(dirname "$config")
  helm repo add democratic-csi https://democratic-csi.github.io/charts/ --force-update
  helm repo add jetstack https://charts.jetstack.io --force-update
  helm repo update
  for protocol in nfs iscsi; do
    local values="$base/csi-$protocol.yaml"
    [[ -s "$values" ]] || die "Missing $values (driver SSH and ZFS configuration)"
    helm upgrade --install "zfs-$protocol" democratic-csi/democratic-csi -n democratic-csi --create-namespace --version "$(require csiVersion)" -f "$values" --wait --timeout 10m
  done
  helm upgrade --install eg oci://docker.io/envoyproxy/gateway-helm -n envoy-gateway-system --create-namespace --version "$(require envoyVersion)" --wait --timeout 10m
  helm upgrade --install cert-manager jetstack/cert-manager -n cert-manager --create-namespace --version "$(require certManagerVersion)" --set crds.enabled=true --set config.enableGatewayAPI=true --wait --timeout 10m
  k3s kubectl apply -f "$base/gateways.yaml"
  k3s kubectl apply -f "$base/issuer.yaml"
  k3s kubectl wait --for=condition=Ready clusterissuer/lets-encrypt --timeout=180s
  k3s kubectl create namespace hosting --dry-run=client -o yaml | k3s kubectl apply -f -
  [[ -s "$base/hosting-values.yaml" ]] || die "Missing hosting-values.yaml"
  helm template admin "$here/../deploy/helm/hostingpanel" -n hosting -f "$base/hosting-values.yaml" >/dev/null
  helm upgrade --install admin "$here/../deploy/helm/hostingpanel" -n hosting -f "$base/hosting-values.yaml" --wait --timeout 10m
}
