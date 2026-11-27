install_node_dependencies() {
  apt-get update
  apt-get install -y curl ca-certificates nfs-common open-iscsi openssl iptables
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
    run_k3s_installer server --cluster-init --secrets-encryption --etcd-snapshot-schedule-cron "0 */6 * * *" --etcd-snapshot-retention 14 --disable traefik --tls-san "$(require clusterHost)"
  fi
  export KUBECONFIG=/etc/rancher/k3s/k3s.yaml
  [[ "$(k3s --version | head -n1 | awk '{print $3}')" == "$(require k3sVersion)" ]] || die "K3s version mismatch; perform a planned upgrade separately"
  wait_for_server_encryption
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
  install -d -m 0700 /etc/rancher/k3s
  install -m 0600 "$token" /etc/rancher/k3s/hosting-agent-token
  export K3S_TOKEN_FILE=/etc/rancher/k3s/hosting-agent-token
  run_k3s_installer agent
  systemctl is-active --quiet k3s-agent
}

# The K3s systemd service can start before API readiness and node annotations.
wait_for_server_encryption() {
  local attempt status="" ready=""
  for ((attempt=0; attempt<60; attempt++)); do
    if [[ -s "$KUBECONFIG" ]] &&
       ready=$(k3s kubectl --request-timeout=5s get --raw=/readyz 2>&1) &&
       status=$(timeout 10s k3s secrets-encrypt status 2>&1); then
      if grep -Eq '^Encryption Status:[[:space:]]+Enabled[[:space:]]*$' <<< "$status"; then
        return 0
      fi
      if grep -Eq '^Encryption Status:[[:space:]]+Disabled[[:space:]]*$' <<< "$status"; then
        die "K3s secrets encryption is disabled"
      fi
    fi
    sleep 5
  done
  printf 'Last API readiness result: %s\nLast encryption status result: %s\n' "$ready" "$status" >&2
  die "Cannot verify K3s secrets encryption; inspect journalctl -u k3s -b"
}
