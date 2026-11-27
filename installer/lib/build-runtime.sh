# Only claim a runtime on a fresh host without existing Docker/containerd packages or units.
build_runtime_present() {
  local package unit
  for package in docker.io docker-ce docker-ce-cli containerd containerd.io moby-engine moby-containerd; do
    if dpkg-query -W -f='${db:Status-Status}' "$package" 2>/dev/null | grep -qx installed; then return 0; fi
  done
  for unit in docker.service docker.socket containerd.service; do
    if [[ $(systemctl show -p LoadState --value "$unit") != not-found ]]; then return 0; fi
  done
  return 1
}

stop_managed_build_runtime() {
  local unit failed=0
  for unit in docker.socket docker.service containerd.service; do
    if [[ $(systemctl show -p LoadState --value "$unit") == loaded ]]; then
      systemctl disable --now "$unit" || failed=1
    fi
  done
  return "$failed"
}

build_panel_image() (
  set -Eeuo pipefail
  local image=$1 marker=$2 managed=false
  if [[ -f "$marker" ]]; then
    managed=true
  elif ! build_runtime_present; then
    # Persist ownership before apt can start services, including interrupted installs.
    install -m 0600 /dev/null "$marker"
    managed=true
  fi
  cleanup_build_runtime() {
    local rc=$? cleanup_rc=0
    trap - EXIT
    if [[ "$managed" == true ]]; then
      stop_managed_build_runtime || cleanup_rc=1
      if ((cleanup_rc)); then
        echo "Failed to stop installer-owned Docker runtime; inspect systemd" >&2
        if ((rc == 0)); then rc=1; fi
      else
        echo "Installer-owned Docker runtime stopped and disabled; K3s remains running."
      fi
    fi
    exit "$rc"
  }
  trap cleanup_build_runtime EXIT
  trap 'exit 130' INT
  trap 'exit 143' TERM
  apt-get install -y docker.io
  systemctl start docker.service
  # Always use the local Docker socket, not an operator's remote Docker context.
  docker --host unix:///var/run/docker.sock build -t "$image" "$here/.."
  docker --host unix:///var/run/docker.sock save "$image" | k3s ctr images import -
)
