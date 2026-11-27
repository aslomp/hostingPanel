#!/usr/bin/env bash
set -Eeuo pipefail
umask 077
[[ $EUID == 0 && $# == 2 ]] || { echo "Usage (root): bash backup-mariadb.sh DESTINATION AGE_RECIPIENT" >&2; exit 1; }
destination=$(realpath "$1")
[[ -d "$destination" && -w "$destination" ]] || exit 1
command -v age >/dev/null
exec 9>/run/lock/hostingpanel-mariadb-backup.lock
flock -n 9
output=$(mktemp "$destination/.mariadb.XXXXXX")
trap 'rm -f -- "$output"' EXIT
# Credentials remain inside the database container; do not use command-line passwords.
k3s kubectl -n hosting exec statefulset/admin-mariadb -- sh -ec '
  export MYSQL_PWD="$MARIADB_ROOT_PASSWORD"
  exec mariadb-dump --user=root --all-databases --single-transaction --quick --routines --events --triggers --hex-blob
' | age -r "$2" -o "$output"
[[ -s "$output" ]]
name="mariadb-$(date -u +%Y%m%dT%H%M%SZ)-$$.sql.age"
mv -T -- "$output" "$destination/$name"
echo "Encrypted logical database backup: $destination/$name"
echo "Transactional consistency requires InnoDB and no concurrent schema changes."
