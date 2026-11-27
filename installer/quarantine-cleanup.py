#!/usr/bin/env python3
"""Expire regular quarantine entries without following links or traversing directories."""
import argparse
import os
import stat
import time

DIRECTORY = "/var/lib/hostingpanel/quarantine"
RETENTION = 30 * 24 * 60 * 60


def cleanup(directory, now, dry_run=False, owner=0):
    # Open relative to a pinned, trusted parent; never follow directory symlinks.
    parent, leaf = os.path.split(directory)
    flags = os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW
    with_fd = os.open(parent, flags)
    try:
        p = os.fstat(with_fd)
        if p.st_uid != owner or p.st_mode & 0o022:
            raise PermissionError("Unsafe quarantine parent")
        fd = os.open(leaf, flags, dir_fd=with_fd)
        try:
            q = os.fstat(fd)
            if q.st_uid != owner or stat.S_IMODE(q.st_mode) != 0o700:
                raise PermissionError("Unsafe quarantine directory")
            removed = 0
            for name in os.listdir(fd):
                try:
                    info = os.stat(name, dir_fd=fd, follow_symlinks=False)
                    # Rename/copy into quarantine updates ctime, unlike preserved mtime.
                    if not stat.S_ISREG(info.st_mode) or info.st_ctime > now - RETENTION:
                        continue
                    if not dry_run:
                        current = os.stat(name, dir_fd=fd, follow_symlinks=False)
                        if (current.st_dev, current.st_ino, current.st_ctime_ns) != (
                                info.st_dev, info.st_ino, info.st_ctime_ns):
                            continue
                        os.unlink(name, dir_fd=fd)
                    print(f"{'Would remove' if dry_run else 'Removed'} {name!r}", flush=True)
                    removed += 1
                except FileNotFoundError:
                    # Another scanner/administrator may have changed the entry.
                    continue
            return removed
        finally:
            os.close(fd)
    finally:
        os.close(with_fd)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()
    if os.geteuid() != 0:
        parser.error("Run as root")
    count = cleanup(DIRECTORY, time.time(), args.dry_run)
    print(f"Quarantine cleanup: {count} expired files")
