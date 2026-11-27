#!/usr/bin/env bash
set -Eeuo pipefail
umask 077
[[ $EUID == 0 && $# == 3 ]] || { echo "Usage (root): bash backup-zfs.sh POOL DESTINATION AGE_RECIPIENT" >&2; exit 1; }
[[ "$1" =~ ^[A-Za-z][A-Za-z0-9_-]*$ ]] || exit 1
dataset="$1/hosting"
destination=$(realpath "$2")
[[ -d "$destination" && -w "$destination" ]] || exit 1
command -v age >/dev/null
zfs list -H "$dataset" >/dev/null
# Reject a destination within the source pool, including child mountpoints.
source=$(findmnt -n -o SOURCE -T "$destination")
[[ "$source" != "$1" && "$source" != "$1/"* ]] || { echo "Backup destination is on source pool" >&2; exit 1; }
exec 9>/run/lock/hostingpanel-zfs-backup.lock
flock -n 9
name="hosting-backup-$(date -u +%Y%m%dT%H%M%SZ)-$$"
snapshot="$dataset@$name"
output=$(mktemp "$destination/.zfs.XXXXXX")
trap 'rm -f -- "$output"' EXIT
zfs snapshot -r "$snapshot"
# A full recursive replication stream preserves dataset properties and descendants.
zfs send -R "$snapshot" | age -r "$3" -o "$output"
[[ -s "$output" ]]
mv -T -- "$output" "$destination/$name.zfs.age"
# Only remove the snapshot created by this invocation, after export succeeds.
zfs destroy -r "$snapshot"
echo "Encrypted ZFS replication backup: $destination/$name.zfs.age"
echo "Live zvol snapshots are crash-consistent, not application-consistent."
