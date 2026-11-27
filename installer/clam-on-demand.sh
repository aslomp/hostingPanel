#!/usr/bin/env bash
set -Eeuo pipefail
umask 077
[[ $EUID == 0 ]] || { echo "Run as root" >&2; exit 2; }
root=/srv/hosting/home
mountpoint -q "$root" || { echo "Hosting dataset not mounted" >&2; exit 2; }
exec 9>/run/lock/hostingpanel-clamav.lock
flock -n 9 || { echo "Scan already running" >&2; exit 2; }
# Scan the live account tree, including hidden files. Never follow symlinks
# outside the tree, scan historical snapshots, or automatically destroy data.
# clamscan owns traversal options; clamdscan delegates engine options to clamd.
status=0
clamscan --recursive=yes --infected --follow-dir-symlinks=0 --follow-file-symlinks=0 --exclude-dir='(^|/)\.zfs(/|$)' --cross-fs=no "$root" || status=$?
case "$status" in
  0) echo "HostingPanel malware scan completed: no detections reported" ;;
  1) echo "HostingPanel malware scan: infected files detected; inspect journal" >&2 ;;
  *) echo "HostingPanel malware scan incomplete or failed (status $status)" >&2; exit 2 ;;
esac
exit "$status"
