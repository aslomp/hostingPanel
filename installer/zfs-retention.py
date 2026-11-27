#!/usr/bin/env python3
"""Create and expire only HostingPanel-owned ZFS snapshots."""
import argparse
import calendar
import datetime as dt
import fcntl
import re
import subprocess

OWNER = "org.hostingpanel:retention"
PREFIX = "hp-retention"
RETENTION = ("hourly", "daily", "weekly")
NAME = re.compile(r"hp-retention-(hourly|daily|weekly)-([0-9]{8}T[0-9]{6}Z)")


def zfs(*args):
    return subprocess.check_output(["zfs", *args], text=True)


def inventory(root):
    result = []
    for line in zfs("list", "-H", "-p", "-r", "-t", "snapshot", "-o",
                    "name,creation," + OWNER, root).splitlines():
        name, created, owner = line.split("\t")
        dataset, _, suffix = name.partition("@")
        match = NAME.fullmatch(suffix)
        if match and owner == "v1" and (dataset == root or dataset.startswith(root + "/")):
            result.append((name, int(created), match[1]))
    return result


def due(tier, now, existing, root):
    # Persistent timers may run after downtime; never fabricate historical snapshots.
    # Weekly snapshots are created only on Sunday UTC, even after a missed timer.
    if tier == "weekly" and now.weekday() != 6:
        return False
    def slot(t):
        if tier == "hourly":
            return t.replace(minute=0, second=0, microsecond=0)
        return t.date()
    return not any(name.startswith(root + "@") and kind == tier
                   and slot(dt.datetime.fromtimestamp(created, dt.timezone.utc)) == slot(now)
                   for name, created, kind in existing)


def expired(existing, now):
    # Calendar-aligned rolling windows; a month may contain four or five Sundays.
    year, month = now.year, now.month - 1
    if month == 0:
        year, month = year - 1, 12
    month_start = now.date().replace(year=year, month=month,
        day=min(now.day, calendar.monthrange(year, month)[1]))
    hour_start = now.replace(minute=0, second=0, microsecond=0) - dt.timedelta(hours=71)
    day_start = now.date() - dt.timedelta(days=6)
    for name, created, tier in existing:
        t = dt.datetime.fromtimestamp(created, dt.timezone.utc)
        if t > now:
            continue
        if ((tier == "hourly" and t < hour_start)
                or (tier == "daily" and t.date() < day_start)
                or (tier == "weekly" and t.date() < month_start)):
            yield name


def run(root, tier, dry_run):
    if not re.fullmatch(r"[A-Za-z][A-Za-z0-9_-]*/hosting", root):
        raise ValueError("Expected POOL/hosting")
    now = dt.datetime.now(dt.timezone.utc)
    existing = inventory(root)
    if due(tier, now, existing, root):
        name = root + "@" + PREFIX + "-" + tier + "-" + now.strftime("%Y%m%dT%H%M%SZ")
        print("Create", name, flush=True)
        if not dry_run:
            zfs("snapshot", "-r", "-o", OWNER + "=v1", name)
            existing = inventory(root)
    failures = []
    for name in expired(existing, now):
        print("Expire", name, flush=True)
        if not dry_run:
            try:
                # Never use -R, -r, -f or deferred deletion: holds and clones must block deletion.
                zfs("destroy", name)
            except subprocess.CalledProcessError:
                failures.append(name)
    if failures:
        raise RuntimeError("Could not expire protected snapshots: " + ", ".join(failures))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("root")
    parser.add_argument("tier", choices=RETENTION)
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()
    with open("/run/lock/hostingpanel-zfs-retention.lock", "a") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        run(args.root, args.tier, args.dry_run)
