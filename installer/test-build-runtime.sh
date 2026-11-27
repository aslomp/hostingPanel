#!/usr/bin/env bash
set -Eeuo pipefail
here=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
source "$here/lib/build-runtime.sh"
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
export log="$tmp/log"
export existing=false fail_build=false
dpkg-query() { if [[ "$existing" == true ]]; then echo installed; else return 1; fi; }
systemctl() {
  if [[ "$1" == show ]]; then
    if [[ "$existing" == true || -f "$tmp/installed" ]]; then echo loaded; else echo not-found; fi
  else
    printf '%s\n' "$*" >> "$log"
  fi
}
apt-get() { touch "$tmp/installed"; }
docker() {
  if [[ "$3" == build && "$fail_build" == true ]]; then return 42; fi
  echo image
}
k3s() { cat >/dev/null; }
marker="$tmp/owner"
build_panel_image sample:latest "$marker"
test -f "$marker"
grep -qx 'disable --now docker.socket' "$log"
grep -qx 'disable --now docker.service' "$log"
grep -qx 'disable --now containerd.service' "$log"
! grep -q k3s "$log"
: > "$log"
export fail_build=true
# Execute in a separate shell so Bash does not suppress errexit inside a conditional.
export -f build_panel_image build_runtime_present stop_managed_build_runtime dpkg-query systemctl apt-get docker k3s
export here tmp marker
set +e
bash -c 'set -Eeuo pipefail; build_panel_image sample:latest "$marker"'
rc=$?
set -e
test "$rc" -eq 42
grep -qx 'disable --now containerd.service' "$log"
rm "$marker"
: > "$log"
export existing=true fail_build=false
build_panel_image sample:latest "$marker"
test ! -e "$marker"
! grep -q disable "$log"
echo "Build runtime ownership and cleanup tests passed."
