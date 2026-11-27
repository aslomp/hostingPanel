#!/usr/bin/env bash
set -Eeuo pipefail
umask 077
here=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
die() { printf '%s\n' "$*" >&2; exit 1; }
mode= config= new=false
while (($#)); do
  case "$1" in
    --full-server|--storage-server|--storage|--worker)
      [[ -z "$mode" ]] || die "Choose exactly one role"
      mode=$1; [[ "$mode" != --storage ]] || mode=--storage-server ;;
    --new-cluster) new=true ;;
    --config) shift; (($#)) || die "Missing config path"; config=$1 ;;
    --help) echo "Usage: bash installer/install.sh --full-server|--storage-server|--worker [--new-cluster] --config FILE"; exit 0 ;;
    *) die "Unknown argument: $1" ;;
  esac
  shift
done
[[ -n "$mode" && -n "$config" ]] || die "Role and config required"
[[ "$new" == false || "$mode" == --full-server ]] || die "--new-cluster requires --full-server"
[[ $EUID == 0 ]] || die "Run as root"
[[ -f "$config" ]] || die "Copy installer/cluster.example.json and configure it first"
command -v python3 >/dev/null || { apt-get update; apt-get install -y python3; }
[[ $(stat -c %u "$config") == 0 ]] || die "Config must be owned by root"
chmod 600 "$config"
config=$(realpath "$config")
# Parse data, never source user configuration as shell code.
get() { python3 -c 'import json,sys; v=json.load(open(sys.argv[1])); print(v[sys.argv[2]])' "$config" "$1"; }
require() { local v; v=$(get "$1"); [[ -n "$v" && "$v" != null ]] || die "Missing $1"; printf '%s' "$v"; }
export DEBIAN_FRONTEND=noninteractive
[[ "$mode" == --worker ]] || python3 "$here/generate.py" "$config"
[[ "$mode" != --full-server || "$new" != true || ! -e /etc/rancher/k3s/k3s.yaml ]] || die "Existing cluster: omit --new-cluster"
[[ "$mode" != --full-server || ! -e /etc/systemd/system/k3s-agent.service ]] || die "This host is a worker"
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
