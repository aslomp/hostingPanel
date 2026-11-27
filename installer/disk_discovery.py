#!/usr/bin/env python3
"""Propose a blank second disk, never select a disk silently."""
import json
import pathlib
import stat
import subprocess
import sys
import os
from generate import write


def candidate(devices):
    disks = [d for d in devices if d.get("type") == "disk"]
    if len(disks) != 2:
        return None
    def blank(d):
        return (not d.get("children") and d.get("ro") is False
                and d.get("rm") is False and not d.get("fstype")
                and not d.get("pttype") and not any(d.get("mountpoints") or []))
    choices = [d for d in disks if blank(d)]
    if len(choices) != 1:
        return None
    other = next(d for d in disks if d is not choices[0])
    def contains_root(d):
        return "/" in (d.get("mountpoints") or []) or any(
            contains_root(c) for c in d.get("children", []))
    return choices[0] if contains_root(other) else None


def inspect():
    devices = json.loads(subprocess.check_output([
        "lsblk", "--json", "--tree", "--paths", "--bytes", "--output",
        "NAME,TYPE,SIZE,RO,RM,FSTYPE,PTTYPE,MOUNTPOINTS"], text=True))["blockdevices"]
    d = candidate(devices)
    if d is None:
        return None
    path = pathlib.Path(d["name"]).resolve(strict=True)
    if not stat.S_ISBLK(path.stat().st_mode):
        return None
    holders = pathlib.Path("/sys/class/block") / path.name / "holders"
    if not holders.is_dir() or any(holders.iterdir()):
        return None
    if subprocess.check_output(["wipefs", "--no-act", "--noheadings", "--output", "TYPE", str(path)], text=True).strip():
        return None
    aliases = sorted(p for p in pathlib.Path("/dev/disk/by-id").glob("*")
                     if p.is_symlink() and p.resolve() == path)
    if not aliases:
        return None
    return str(aliases[0]), str(path), int(d["size"]), path.stat().st_rdev


def main():
    config = pathlib.Path(sys.argv[1])
    c = json.loads(config.read_text())
    if c["poolDevices"] or c["confirmPoolCreation"]:
        return
    if not sys.stdin.isatty():
        raise SystemExit("No interactive terminal; explicitly configure the ZFS disk.")
    found = inspect()
    if found is None:
        raise SystemExit("No unambiguous unused second disk found; create/import a pool or configure a disk explicitly.")
    alias, path, size, _ = found
    print(f"Unused second disk: {path} ({size / 1024**3:.1f} GiB)")
    print(f"Stable device: {alias}")
    print(f"Proposed ZFS pool: {c['pool']}. The system disk will not be modified.")
    print("Creating a ZFS pool overwrites the selected disk. Ensure it contains no valuable data.")
    if input(f"Type {path} to confirm, or press Enter to cancel: ").strip() != path:
        raise SystemExit("Disk initialization cancelled.")
    if inspect() != found:
        raise SystemExit("Disk inventory changed; refusing initialization.")
    c["poolDevices"] = [alias]
    c["confirmPoolCreation"] = True
    write(config, c)


if __name__ == "__main__":
    main()
