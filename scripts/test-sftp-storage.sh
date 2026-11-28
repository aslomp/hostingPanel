#!/usr/bin/env bash
# Run only against two disposable hosting accounts. Requires OpenSSH client.
set -euo pipefail
: "${SFTP_HOST:?}" "${SFTP_PORT:?}" "${SFTP_USER_A:?}" "${SFTP_USER_B:?}" "${SFTP_KEY_A:?}" "${SFTP_KEY_B:?}" "${SFTP_KNOWN_HOSTS:?}" "${SFTP_ROOT_A:?}" "${SFTP_ROOT_B:?}" "${SFTP_UID_A:?}" "${SFTP_GID_A:?}"
[[ "$SFTP_USER_A" != "$SFTP_USER_B" && "$SFTP_ROOT_A" != "$SFTP_ROOT_B" ]]
tmp=$(mktemp -d)
name="hostingpanel-acceptance-$(basename "$tmp")"
trap 'rm -rf -- "$tmp"' EXIT
printf 'test-a\n' > "$tmp/a"
printf 'test-b\n' > "$tmp/b"
sftp_run() {
  local user=$1 key=$2
  sftp -q -b - -P "$SFTP_PORT" -i "$key" -o BatchMode=yes -o IdentitiesOnly=yes -o StrictHostKeyChecking=yes -o UserKnownHostsFile="$SFTP_KNOWN_HOSTS" "$user@$SFTP_HOST"
}
printf 'put %s %s\n' "$tmp/a" "$name" | sftp_run "$SFTP_USER_A" "$SFTP_KEY_A"
printf 'put %s %s\n' "$tmp/b" "$name" | sftp_run "$SFTP_USER_B" "$SFTP_KEY_B"
[[ $(stat -c '%u:%g' "$SFTP_ROOT_A/$name") == "$SFTP_UID_A:$SFTP_GID_A" ]]
cmp "$tmp/a" "$SFTP_ROOT_A/$name"
cmp "$tmp/b" "$SFTP_ROOT_B/$name"
printf 'get %s %s\n' "$name" "$tmp/observed" | sftp_run "$SFTP_USER_A" "$SFTP_KEY_A"
cmp "$tmp/a" "$tmp/observed"
ln -s "$SFTP_ROOT_B/$name" "$SFTP_ROOT_A/$name-link"
if printf 'get %s %s\n' "$name-link" "$tmp/escaped" | sftp_run "$SFTP_USER_A" "$SFTP_KEY_A"; then
  echo 'FAIL: external symlink readable' >&2; exit 1
fi
rm -- "$SFTP_ROOT_A/$name-link"
printf 'rm %s\n' "$name" | sftp_run "$SFTP_USER_A" "$SFTP_KEY_A"
printf 'rm %s\n' "$name" | sftp_run "$SFTP_USER_B" "$SFTP_KEY_B"
echo 'Basic SFTP acceptance passed; not proof of race-free confinement.'
