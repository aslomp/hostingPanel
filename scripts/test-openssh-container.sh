#!/usr/bin/env bash
# Isolated Docker acceptance test; run on a disposable CI runner.
set -euo pipefail
tmp=$(mktemp -d)
container=hosting-openssh-smoke
trap 'docker logs "$container" 2>&1 || true; docker rm -f "$container" >/dev/null 2>&1 || true; sudo rm -rf "$tmp"' EXIT
a=sftp-00000000000000000000000000000001
b=sftp-00000000000000000000000000000002
c=sftp-00000000000000000000000000000003
mkdir "$tmp/hostkeys" "$tmp/config" "$tmp/a" "$tmp/b" "$tmp/c"
ssh-keygen -q -t ed25519 -N '' -f "$tmp/hostkeys/ssh_host_ed25519_key"
for key in a b c; do ssh-keygen -q -t ed25519 -N '' -f "$tmp/key-$key"; done
sudo chown -R 10042:10042 "$tmp/a" "$tmp/c"
sudo chown -R 10043:10043 "$tmp/b"
sudo chmod 700 "$tmp/a" "$tmp/b" "$tmp/c"
sudo chown root:root "$tmp/hostkeys/ssh_host_ed25519_key"
sudo chmod 400 "$tmp/hostkeys/ssh_host_ed25519_key"
python3 - "$tmp" "$a" "$b" "$c" <<'PY'
import json,pathlib,sys
p=pathlib.Path(sys.argv[1])
rows=[dict(login=login,uid=uid,gid=uid,keys=[(p/f"key-{key}.pub").read_text().strip()],path=key)
      for login,uid,key in zip(sys.argv[2:],(10042,10043,10042),("a","b","c"))]
(p/"config/accounts.json").write_text(json.dumps(rows,separators=(",",":")))
PY
revision=$(sha256sum "$tmp/config/accounts.json" | cut -d' ' -f1)
printf '[127.0.0.1]:22022 %s\n' "$(cut -d' ' -f1,2 "$tmp/hostkeys/ssh_host_ed25519_key.pub")" > "$tmp/known_hosts"
docker build -f Dockerfile.openssh -t hostingpanel-openssh:ci .
docker run -d --name "$container" --cap-drop ALL --cap-add SETUID --cap-add SETGID --cap-add SYS_CHROOT --cap-add KILL --security-opt no-new-privileges -p 127.0.0.1:22022:2022 -e HP_SFTP_REVISION="$revision" -v "$tmp/hostkeys:/run/hostingpanel-hostkeys:ro" -v "$tmp/config:/etc/hostingpanel:ro" -v "$tmp/a:/jails/$a/data" -v "$tmp/b:/jails/$b/data" -v "$tmp/c:/jails/$c/data" hostingpanel-openssh:ci
opts=(-o BatchMode=yes -o IdentitiesOnly=yes -o StrictHostKeyChecking=yes -o "UserKnownHostsFile=$tmp/known_hosts")
sftp_run() { local user=$1 key=$2; sftp -b - -P 22022 "${opts[@]}" -i "$tmp/key-$key" "$user@127.0.0.1"; }
for attempt in $(seq 1 30); do
  if printf 'pwd\n' | sftp_run "$a" a >/dev/null 2>&1; then break; fi
  sleep 1
done
printf 'payload\n' > "$tmp/input"
for pair in "$a:a" "$b:b" "$c:c"; do
  login=${pair%:*}; key=${pair#*:}
  printf 'put %s %s\n' "$tmp/input" "only-$key" | sftp_run "$login" "$key"
done
test "$(sudo stat -c '%u:%g' "$tmp/a/only-a")" = 10042:10042
test "$(sudo stat -c '%u:%g' "$tmp/b/only-b")" = 10043:10043
test "$(sudo stat -c '%u:%g' "$tmp/c/only-c")" = 10042:10042
if printf 'get /data/only-b %s\n' "$tmp/escape" | sftp_run "$a" a; then exit 1; fi
if printf 'get /data/only-c %s\n' "$tmp/escape" | sftp_run "$a" a; then exit 1; fi
if printf 'pwd\n' | sftp_run "$a" b; then exit 1; fi
if printf 'get /etc/passwd %s\n' "$tmp/escape" | sftp_run "$a" a; then exit 1; fi
if ssh -p 22022 "${opts[@]}" -i "$tmp/key-a" "$a@127.0.0.1" id; then exit 1; fi
sudo ln -s /etc/passwd "$tmp/a/outside"
if printf 'get outside %s\n' "$tmp/escape" | sftp_run "$a" a; then exit 1; fi
printf 'get only-a %s\n' "$tmp/output" | sftp_run "$a" a
cmp "$tmp/input" "$tmp/output"
echo "OpenSSH native UID/GID and per-login chroot smoke passed"
