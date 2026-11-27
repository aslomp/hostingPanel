#!/usr/bin/env bash
set -Eeuo pipefail
umask 077
here=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
die() { printf '%s\n' "$*" >&2; exit 1; }
mode= config= new=false default_config=false
while (($#)); do
  case "$1" in
    --full-server|--storage-server|--storage|--worker)
      [[ -z "$mode" ]] || die "Choose exactly one role"
      mode=$1; [[ "$mode" != --storage ]] || mode=--storage-server ;;
    --new-cluster) new=true ;;
    --config) shift; (($#)) || die "Missing config path"; config=$1 ;;
    --help) echo "Usage: bash installer/install.sh --full-server|--storage-server|--worker [--new-cluster] [--config FILE]"; exit 0 ;;
    *) die "Unknown argument: $1" ;;
  esac
  shift
done
[[ -n "$mode" ]] || die "Role required"
[[ "$new" == false || "$mode" == --full-server ]] || die "--new-cluster requires --full-server"
[[ $EUID == 0 ]] || die "Run as root"
# Parse data, never source user configuration as shell code.
get() { python3 -c 'import json,sys; v=json.load(open(sys.argv[1])); print(v[sys.argv[2]])' "$config" "$1"; }
require() { local v; v=$(get "$1"); [[ -n "$v" && "$v" != null ]] || die "Missing $1"; printf '%s' "$v"; }
export DEBIAN_FRONTEND=noninteractive
exec 9>/run/lock/hostingpanel-installer.lock
flock -n 9 || die "Another installer is running"
[[ -d /run/systemd/system ]] || die "A systemd Linux host is required"
source /etc/os-release
case "$ID:$VERSION_ID" in
  ubuntu:24.04|ubuntu:26.04|debian:12|debian:13) ;;
  *) die "Unsupported host distribution: $ID $VERSION_ID" ;;
esac
[[ $(uname -m) == x86_64 || $(uname -m) == aarch64 ]] || die "Unsupported CPU architecture"
trap 'printf "Installation failed at %s:%s (exit %s). Inspect systemd and Kubernetes logs; no automatic deletion is performed.\\n" "${BASH_SOURCE[0]}" "$LINENO" "$?" >&2' ERR
command -v python3 >/dev/null && command -v ip >/dev/null || { apt-get update; apt-get install -y python3 iproute2; }
if [[ -z "$config" ]]; then
  default_config=true
  base=/etc/hostingpanel/installer
  [[ ! -L "$base" && ! -L /etc/hostingpanel ]] || die "Unsafe default configuration path"
  install -d -m 0700 -o root -g root "$base"
  if [[ "$mode" == --worker ]]; then
    config="$base/worker.json"
    [[ -f "$config" ]] || die "Copy worker.json and agent-token from the control plane to $base"
  else
    config="$base/cluster.json"
    if [[ ! -e "$config" ]]; then
      python3 "$here/defaults.py" "$config" "$mode"
    fi
  fi
fi
[[ -f "$config" && ! -L "$config" ]] || die "Configuration file missing or unsafe"
config=$(realpath "$config")
[[ $(stat -c %u "$config") == 0 ]] || die "Config must be owned by root"
[[ $(stat -c %u "$(dirname "$config")") == 0 ]] || die "Config directory must be owned by root"
[[ $(stat -c %a "$(dirname "$config")") == 700 ]] || die "Config directory must have mode 0700"
chmod 600 "$config"
if [[ "$mode" == --full-server ]]; then
  if [[ "$default_config" == true ]]; then
    python3 "$here/defaults.py" "$config" --complete
  else
    [[ -n $(get acmeEmail) ]] || die "Explicit configuration requires acmeEmail"
  fi
fi
if [[ "$mode" == --worker ]]; then
  python3 -c 'import json,sys,ipaddress,re; c=json.load(open(sys.argv[1])); assert ipaddress.ip_address(c["clusterHost"]).version==4; assert re.fullmatch(r"v\d+\.\d+\.\d+\+k3s\d+",c["k3sVersion"]); assert c["agentTokenFile"]=="agent-token"' "$config"
fi
[[ "$mode" == --worker ]] || python3 "$here/generate.py" "$config"
[[ "$mode" != --full-server || "$new" != true || ! -e /etc/rancher/k3s/k3s.yaml ]] || die "Existing cluster: omit --new-cluster"
[[ "$mode" != --full-server || ! -e /etc/systemd/system/k3s-agent.service ]] || die "This host is a worker"
source "$here/lib/clamav.sh"
source "$here/lib/storage.sh"
source "$here/lib/k3s.sh"
source "$here/lib/platform.sh"
case "$mode" in
  --storage-server) install_storage ;;
  --worker) install_worker ;;
  --full-server)
    if [[ $(get localStorage) == True ]]; then install_storage; fi
    install_server
    install_platform ;;
esac
