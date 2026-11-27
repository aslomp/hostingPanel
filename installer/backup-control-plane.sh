#!/usr/bin/env bash
set -Eeuo pipefail
umask 077
[[ $EUID == 0 ]] || { echo "Run as root" >&2; exit 1; }
[[ $# == 2 ]] || { echo "Usage: bash backup-control-plane.sh DESTINATION AGE_RECIPIENT" >&2; exit 1; }
destination=$(realpath "$1")
recipient=$2
[[ -d "$destination" && -w "$destination" ]] || { echo "Backup destination unavailable" >&2; exit 1; }
command -v age >/dev/null || { echo "Install age first" >&2; exit 1; }
exec 9>/run/lock/hostingpanel-backup.lock
flock -n 9
tmp=$(mktemp -d)
output=
trap 'rm -rf -- "$tmp"; if [[ -n "$output" ]]; then rm -f -- "$output"; fi' EXIT
name="hosting-$(date -u +%Y%m%dT%H%M%SZ)-$$"
k3s etcd-snapshot save --name "$name" --etcd-snapshot-dir "$tmp"
install -m 0600 /var/lib/rancher/k3s/server/token "$tmp/server-token"
k3s --version > "$tmp/k3s-version"
# Snapshot and token together are sensitive even when Kubernetes Secrets are encrypted.
output=$(mktemp "$destination/.$name.XXXXXX")
tar -C "$tmp" -cf - . | age -r "$recipient" -o "$output"
[[ -s "$output" ]]
mv -T -- "$output" "$destination/$name.tar.age"
output=
echo "Encrypted control-plane export: $destination/$name.tar.age"
echo "This does not back up application PVCs or ZFS datasets."
