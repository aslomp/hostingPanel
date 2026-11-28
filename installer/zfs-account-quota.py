#!/usr/bin/python3
"""Root-owned forced SSH command. Only quota operations on the configured home dataset."""
import os
import pathlib
import re
import stat
import subprocess
import sys
import fcntl

def run(*args):
    return subprocess.check_output(["/usr/sbin/zfs", *args], text=True, timeout=15).strip()

def main():
    dataset = pathlib.Path("/etc/hostingpanel/quota-dataset").read_text().strip()
    if not re.fullmatch(r"[A-Za-z][A-Za-z0-9_-]*/hosting/home", dataset):
        raise ValueError("invalid configured dataset")
    parts = os.environ.get("SSH_ORIGINAL_COMMAND", "").split()
    if len(parts)!=5 or parts[0]!="apply":
        raise ValueError("invalid command")
    _, user, uid_text, quota, objects = parts
    if not re.fullmatch(r"[A-Za-z0-9_-]{1,64}",user) or not re.fullmatch(r"[0-9]{1,10}",uid_text):
        raise ValueError("invalid account")
    uid=int(uid_text)
    if not 1000<=uid<2147483647:
        raise ValueError("invalid uid")
    for value in (quota,objects):
        if value!="none" and (not re.fullmatch(r"[1-9][0-9]{0,18}",value) or int(value)>9223372036854775807):
            raise ValueError("invalid limit")
    root=pathlib.Path("/srv/hosting/home")
    if run("get","-H","-o","value","mountpoint",dataset)!=str(root) or not os.path.ismount(root):
        raise ValueError("dataset unavailable")
    with open("/run/hostingpanel-quota.lock","a") as lock:
        fcntl.flock(lock,fcntl.LOCK_EX)
        home=root/user
        info=home.lstat()
        if not stat.S_ISDIR(info.st_mode) or info.st_uid!=uid or info.st_dev!=root.stat().st_dev:
            raise ValueError("ownership mismatch")
        for entry in root.iterdir():
            if entry!=home and entry.name!=".zfs":
                other=entry.lstat()
                if stat.S_ISDIR(other.st_mode) and other.st_uid==uid:
                    raise ValueError("shared UID")
        for prop,value in (("userquota",quota),("userobjquota",objects)):
            key=f"{prop}@{uid}"
            observed=run("get","-Hp","-o","value",key,dataset)
            if observed not in (("none","0") if value=="none" else (value,)):
                run("set",f"{key}={value}",dataset)
            observed=run("get","-Hp","-o","value",key,dataset)
            if observed not in (("none","0") if value=="none" else (value,)):
                raise ValueError("quota readback mismatch")
    print("OK")

if __name__=="__main__":
    try:
        main()
    except Exception as exc:
        print("quota request rejected: "+type(exc).__name__,file=sys.stderr)
        sys.exit(1)
