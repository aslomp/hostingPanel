#!/usr/bin/env bash
# Isolated Docker acceptance test; run on a disposable CI runner.
set -euo pipefail
tmp=$(mktemp -d)
container=hosting-openssh-smoke
trap 'docker logs "$container" 2>&1 || true; docker rm -f "$container" >/dev/null 2>&1 || true; sudo rm -rf "$tmp"' EXIT
a=sftp-00000000000000000000000000000001
b=sftp-00000000000000000000000000000002
c=sftp-00000000000000000000000000000003
mkdir "$tmp/hostkeys" "$tmp/config" "$tmp/jails"
mkdir -p "$tmp/jails/$a/data" "$tmp/jails/$b/data" "$tmp/jails/$c/data"
sudo chown root:root "$tmp/jails" "$tmp/jails/$a" "$tmp/jails/$b" "$tmp/jails/$c"
sudo chmod 0755 "$tmp/jails" "$tmp/jails/$a" "$tmp/jails/$b" "$tmp/jails/$c"
ln -s "jails/$a/data" "$tmp/a"
ln -s "jails/$b/data" "$tmp/b"
ln -s "jails/$c/data" "$tmp/c"
ssh-keygen -q -t ed25519 -N '' -f "$tmp/hostkeys/ssh_host_ed25519_key"
for key in a b c; do ssh-keygen -q -t ed25519 -N '' -f "$tmp/key-$key"; done
sudo chown -R 10042:10042 "$tmp/jails/$a/data" "$tmp/jails/$c/data"
sudo chown -R 10043:10043 "$tmp/jails/$b/data"
sudo chmod 700 "$tmp/a" "$tmp/b" "$tmp/c"
sudo chown root:root "$tmp/hostkeys/ssh_host_ed25519_key"
sudo chmod 400 "$tmp/hostkeys/ssh_host_ed25519_key"
write_accounts() {
  sudo python3 - "$tmp" "$a" "$b" "$c" "$1" <<'PY'
import pathlib,sys,os
p=pathlib.Path(sys.argv[1]); config=p/"config"
rows=list(zip(sys.argv[2:5],(10042,10043,10042),("a","b","c")))[:int(sys.argv[5])]
new=config/("generation-"+sys.argv[5]);new.mkdir()
(new/"passwd").write_text("root:x:0:0:root:/root:/bin/sh\nsshd:x:74:74:sshd:/run/sshd:/usr/sbin/nologin\n"+"".join(f"{login}:x:{uid}:{uid}::/data:/usr/sbin/nologin\n" for login,uid,key in rows))
(new/"group").write_text("root:x:0:\nsshd:x:74:\n"+"".join(f"hpsftp-{uid}:x:{uid}:\n" for uid in sorted({uid for _,uid,_ in rows})))
(new/"shadow").write_text("root:*:20000:0:99999:7:::\nsshd:*:20000:0:99999:7:::\n"+"".join(f"{login}:*:20000:0:99999:7:::\n" for login,uid,key in rows))
for login,uid,key in rows:
    (new/("keys-"+login)).write_text("restrict "+(p/f"key-{key}.pub").read_text())
for name in ["passwd","group","shadow"]+["keys-"+login for login,_,_ in rows]:
    target=config/name
    if not target.is_symlink(): target.symlink_to("..data/"+name)
(config/"..next").symlink_to(new.name)
os.replace(config/"..next",config/"..data")
PY
  sudo chown -R root:root "$tmp/config"
  sudo chmod -R a+rX "$tmp/config"
}
write_accounts 2
printf '[127.0.0.1]:22022 %s\n' "$(cut -d' ' -f1,2 "$tmp/hostkeys/ssh_host_ed25519_key.pub")" > "$tmp/known_hosts"
docker build -f Dockerfile.openssh -t hostingpanel-openssh:ci .
docker run -d --name "$container" --cap-drop ALL --cap-add SETUID --cap-add SETGID --cap-add SYS_CHROOT --cap-add KILL --security-opt no-new-privileges -p 127.0.0.1:22022:2022 -v "$tmp/hostkeys:/run/hostingpanel-hostkeys:ro" -v "$tmp/config:/etc/hostingpanel:ro" -v "$tmp/jails:/jails" hostingpanel-openssh:ci
opts=(-o BatchMode=yes -o IdentitiesOnly=yes -o StrictHostKeyChecking=yes -o "UserKnownHostsFile=$tmp/known_hosts")
sftp_run() { local user=$1 key=$2; stdbuf -oL -eL sftp -b - -P 22022 "${opts[@]}" -i "$tmp/key-$key" "$user@127.0.0.1"; }
for attempt in $(seq 1 30); do
  if printf 'pwd\n' | sftp_run "$a" a >/dev/null 2>&1; then break; fi
  sleep 1
done
printf 'payload\n' > "$tmp/input"
# Add a third login while a real SFTP session remains open.
mkfifo "$tmp/commands"
sftp_run "$a" a < "$tmp/commands" > "$tmp/session.log" 2>&1 &
session=$!
exec 9>"$tmp/commands"
printf 'pwd\n' >&9
for attempt in $(seq 1 30); do
  if grep -q 'Remote working directory:' "$tmp/session.log"; then break; fi
  sleep 1
done
grep -q 'Remote working directory:' "$tmp/session.log"
pid_before=$(docker inspect -f '{{.State.Pid}}' "$container")
# The projected volume is root-owned; the test writer is privileged only on the CI host.
write_accounts 3
printf 'put %s %s\n' "$tmp/input" "session-survived" >&9
printf 'bye\n' >&9
exec 9>&-
wait "$session"
test "$(docker inspect -f '{{.State.Pid}}' "$container")" = "$pid_before"
test "$(sudo stat -c '%u:%g' "$tmp/a/session-survived")" = 10042:10042
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
