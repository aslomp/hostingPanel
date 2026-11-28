"""Materialize only platform-generated identities before starting OpenSSH."""
import base64
import hashlib
import json
import os
import pathlib
import re
import stat
import subprocess

def valid_key(key):
    if not isinstance(key, str) or any(ord(c)<32 or ord(c)==127 for c in key):
        return False
    parts=key.split()
    if len(parts)<2 or parts[0]!="ssh-ed25519":
        return False
    try:
        blob=base64.b64decode(parts[1],validate=True)
        return len(blob)==51 and blob[:19]==b"\x00\x00\x00\x0bssh-ed25519\x00\x00\x00\x20"
    except ValueError:
        return False

def verify_jail(login,uid,gid):
    jail=pathlib.Path("/jails")/login
    for path in (pathlib.Path("/"),jail.parent,jail):
        info=path.lstat()
        if not stat.S_ISDIR(info.st_mode) or info.st_uid!=0 or info.st_mode&0o022:
            raise ValueError("unsafe root-owned chroot")
    data=jail/"data"
    info=data.lstat()
    if not stat.S_ISDIR(info.st_mode) or (info.st_uid,info.st_gid)!=(uid,gid) or not os.path.ismount(data):
        raise ValueError("missing or mismatched NFS subPath mount")

def main():
    os.umask(0o022)
    raw=pathlib.Path("/etc/hostingpanel/accounts.json").read_bytes()
    revision=os.environ["HP_SFTP_REVISION"]
    if not ((revision=="bootstrap" and raw.strip()==b"[]") or hashlib.sha256(raw).hexdigest()==revision):
        raise ValueError("snapshot revision mismatch")
    rows=json.loads(raw)
    if not isinstance(rows,list):
        raise ValueError("invalid snapshot")
    passwd=pathlib.Path("/etc/passwd")
    group=pathlib.Path("/etc/group")
    shadow=pathlib.Path("/etc/shadow")
    base_passwd=[line for line in passwd.read_text().splitlines() if not line.startswith("sftp-")]
    base_group=[line for line in group.read_text().splitlines() if not line.startswith("hpsftp-")]
    base_shadow=[line for line in shadow.read_text().splitlines() if not line.startswith("sftp-")]
    system_uids={int(line.split(":")[2]) for line in base_passwd}
    system_gids={int(line.split(":")[2]) for line in base_group}
    users=set()
    groups=set()
    keys={}
    for row in rows:
        login,uid,gid,public_keys=row["login"],row["uid"],row["gid"],row["keys"]
        if not re.fullmatch(r"sftp-[0-9a-f]{32}",login) or login in users:
            raise ValueError("invalid login")
        if type(uid) is not int or type(gid) is not int or not 1000<=uid<2147483647 or not 1000<=gid<2147483647 or uid in system_uids or gid in system_gids:
            raise ValueError("invalid Unix identity")
        if not isinstance(public_keys,list) or not public_keys or not all(valid_key(key) for key in public_keys):
            raise ValueError("invalid public keys")
        verify_jail(login,uid,gid)
        users.add(login)
        groups.add(gid)
        # Invalid password hash (not Linux's ! lock prefix); public-key authentication only.
        base_passwd.append(f"{login}:x:{uid}:{gid}::/data:/usr/sbin/nologin")
        base_shadow.append(f"{login}:*:20000:0:99999:7:::")
        keys[login]="".join("restrict "+key+"\n" for key in public_keys)
    for gid in sorted(groups):
        base_group.append(f"hpsftp-{gid}:x:{gid}:")
    for path,lines in ((passwd,base_passwd),(group,base_group),(shadow,base_shadow)):
        path.write_text("\n".join(lines)+"\n")
    keydir=pathlib.Path("/run/hostingpanel-keys")
    for entry in keydir.iterdir():
        entry.unlink()
    for login,content in keys.items():
        path=keydir/login
        path.write_text(content)
        path.chmod(0o644)
    subprocess.run(["/usr/sbin/sshd","-t","-f","/etc/ssh/hostingpanel_sshd_config"],check=True)
    os.execv("/usr/sbin/sshd",["sshd","-D","-e","-f","/etc/ssh/hostingpanel_sshd_config"])

if __name__=="__main__":
    main()
