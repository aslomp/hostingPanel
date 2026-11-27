install_node_dependencies() {
  apt-get update
  apt-get install -y curl ca-certificates nfs-common open-iscsi openssl
  modprobe iscsi_tcp
  printf 'iscsi_tcp\n' > /etc/modules-load.d/hostingpanel-iscsi.conf
  systemctl enable --now iscsid
}
run_k3s_installer() {
  local script
  script=$(mktemp)
  curl --fail --silent --show-error --location https://get.k3s.io -o "$script"
  INSTALL_K3S_VERSION="$(require k3sVersion)" sh "$script" "$@"
  rm -f "$script"
}
install_server() {
  if [[ "$new" == true && -e /etc/rancher/k3s/k3s.yaml ]]; then
    die "Existing cluster: omit --new-cluster to reconcile"
  fi
  install_node_dependencies
  if [[ ! -e /etc/rancher/k3s/k3s.yaml ]]; then
    [[ "$new" == true ]] || die "Use --new-cluster to initialize"
    run_k3s_installer server --cluster-init --secrets-encryption --disable traefik --tls-san "$(require clusterHost)"
  fi
  export KUBECONFIG=/etc/rancher/k3s/k3s.yaml
  k3s secrets-encrypt status
  [[ "$(k3s --version | head -n1 | awk '{print $3}')" == "$(require k3sVersion)" ]] || die "K3s version mismatch; perform a planned upgrade separately"
  k3s kubectl wait --for=condition=Ready node --all --timeout=300s
  install -m 0600 /var/lib/rancher/k3s/server/agent-token "$(dirname "$config")/agent-token"
  python3 -c 'import json,sys; c=json.load(open(sys.argv[1])); json.dump({k:c[k] for k in ("clusterHost","k3sVersion","agentTokenFile")},sys.stdout,indent=2)' "$config" > "$(dirname "$config")/worker.json"
}
install_worker() {
  install_node_dependencies
  [[ ! -e /etc/rancher/k3s/k3s.yaml ]] || die "This host is already a server"
  local token
  token=$(require agentTokenFile)
  [[ "$token" == /* ]] || token="$(dirname "$config")/$token"
  [[ -s "$token" && $(stat -c %u "$token") == 0 && $(stat -c %a "$token") == 600 ]] || die "Missing or insecure agent token"
  grep -q "^K10" "$token" || die "Secure K3s token with CA hash required"
  export K3S_URL="https://$(require clusterHost):6443"
  export K3S_TOKEN_FILE="$token"
  run_k3s_installer agent
  systemctl is-active --quiet k3s-agent
}
