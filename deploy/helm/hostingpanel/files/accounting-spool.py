"""Apache piped logger: immutable durable segments, retry until committed acknowledgement."""
import datetime
import fcntl
import os
import pathlib
import queue
import re
import select
import sys
import threading
import time
import urllib.request
import uuid

ROOT = pathlib.Path("/storage/accounting-spool")
VALID = re.compile(r"[0-9a-f]{32}\.ready")
PREFIX = ("HP_ACCESS_V1 ", "HP_ERROR_V1 ")
MAX_BYTES = 1048576

def sync_dir(root):
    fd = os.open(root, os.O_RDONLY | os.O_DIRECTORY)
    try:
        os.fsync(fd)
    finally:
        os.close(fd)

class Journal:
    """One locked, append-only segment. Every complete record is fsynced."""
    def __init__(self, root):
        self.root = root
        ident = uuid.uuid4().hex
        self.pending = root / (ident + ".open")
        self.ready = root / (ident + ".ready")
        self.file = self.pending.open("xb", buffering=0)
        fcntl.flock(self.file, fcntl.LOCK_EX)
        sync_dir(root)

    def append(self, record):
        if not record.endswith(b"\n"):
            raise ValueError("incomplete record")
        remaining = memoryview(record)
        while remaining:
            written = self.file.write(remaining)
            if not written:
                raise OSError("journal write failed")
            remaining = remaining[written:]
        os.fsync(self.file.fileno())

    def close(self):
        os.rename(self.pending, self.ready)
        sync_dir(self.root)
        self.file.close()
        return self.ready

def seal(root, records):
    journal = Journal(root)
    for record in records:
        journal.append(record)
    return journal.close()

def recover(root):
    """Recover orphaned journals, never touch a currently locked writer."""
    for pending in root.iterdir():
        if not re.fullmatch(r"[0-9a-f]{32}\.open", pending.name) or pending.is_symlink():
            continue
        try:
            with pending.open("r+b", buffering=0) as file:
                try:
                    fcntl.flock(file, fcntl.LOCK_EX | fcntl.LOCK_NB)
                except BlockingIOError:
                    continue
                if not pending.exists():
                    continue
                body = file.read(MAX_BYTES + 1)
                if len(body) > MAX_BYTES:
                    raise ValueError("oversized orphan journal")
                end = body.rfind(b"\n") + 1
                if end != len(body):
                    print("accounting recovery: incomplete trailing record", file=sys.stderr, flush=True)
                if not end:
                    # Preserve evidence rather than submitting an empty segment.
                    pending.rename(pending.with_suffix(".incomplete"))
                else:
                    file.truncate(end)
                    os.fsync(file.fileno())
                    pending.rename(pending.with_suffix(".ready"))
                sync_dir(root)
        except FileNotFoundError:
            continue

class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None

def deliver(path, endpoint, token):
    # Multiple logger processes can discover the same segment at startup.
    # The receiver is idempotent; the NFS lock also prevents concurrent deletion.
    try:
        with path.open("rb") as file:
            fcntl.flock(file, fcntl.LOCK_EX)
            if not path.exists():
                return
            body = file.read(MAX_BYTES + 1)
            if not body or len(body) > MAX_BYTES:
                raise ValueError("invalid spool segment")
            req = urllib.request.Request(
                endpoint.rstrip("/") + "/hosting/internal/apache-accounting/" + str(uuid.UUID(hex=path.stem)),
                data=body, headers={"X-HostingPanel-Accounting-Token": token,
                                    "Content-Type": "application/octet-stream"}, method="POST")
            with urllib.request.build_opener(NoRedirect()).open(req, timeout=30) as reply:
                if reply.status != 204:
                    raise RuntimeError("accounting not acknowledged")
            path.unlink(missing_ok=True)
            sync_dir(path.parent)
    except FileNotFoundError:
        return

def sender(work, endpoint, token):
    while True:
        path = work.get()
        if path is None:
            return
        delay = 1
        while True:
            try:
                deliver(path, endpoint, token)
                break
            except Exception as exc:
                print("accounting segment retained: " + type(exc).__name__, file=sys.stderr, flush=True)
                time.sleep(delay)
                delay = min(delay * 2, 60)

def encode(raw):
    text = raw.decode("utf-8", errors="strict").rstrip("\r\n")
    if not text.startswith(PREFIX):
        return None
    if len(text) > 16000 or any(ord(c) < 32 or ord(c) == 127 for c in text):
        raise ValueError("invalid Apache accounting line")
    stamp = datetime.datetime.now(datetime.timezone.utc).isoformat(timespec="microseconds")
    # .NET round-trip format has seven fractional digits.
    stamp = stamp[:-6] + "0" + stamp[-6:]
    return (stamp + " " + text + "\n").encode("utf-8")

def main():
    os.umask(0o077)
    endpoint = os.environ["HP_ACCOUNTING_ENDPOINT"]
    token = os.environ["HP_ACCOUNTING_TOKEN"]
    if not endpoint.startswith("https://") or len(token) < 32:
        raise RuntimeError("accounting requires HTTPS and dedicated token")
    if not ROOT.is_dir():
        raise RuntimeError("accounting spool missing")
    work = queue.Queue(maxsize=1024)
    thread = threading.Thread(target=sender, args=(work, endpoint, token), daemon=True)
    thread.start()
    # Recover sealed segments from any previous replica incarnation. No idle scan.
    recover(ROOT)
    for path in ROOT.iterdir():
        if VALID.fullmatch(path.name) and not path.is_symlink():
            work.put(path)
    # Read raw pipe bytes; select must not be used with buffered readline.
    buffer = b""
    journal = None
    count = 0
    size = 0
    deadline = None
    def flush():
        nonlocal journal, count, size, deadline
        if journal is not None:
            ready = journal.close()
            journal, count, size, deadline = None, 0, 0, None
            work.put(ready)
    while True:
        timeout = None if deadline is None else max(0, deadline - time.monotonic())
        readable, _, _ = select.select([0], [], [], timeout)
        if not readable:
            flush()
            continue
        chunk = os.read(0, 65536)
        if not chunk:
            flush()
            break
        buffer += chunk
        while b"\n" in buffer:
            raw, buffer = buffer.split(b"\n", 1)
            try:
                record = encode(raw)
            except (UnicodeError, ValueError):
                print("invalid accounting line dropped", file=sys.stderr, flush=True)
                continue
            if record is None:
                continue
            if size + len(record) > MAX_BYTES:
                flush()
            if journal is None:
                journal = Journal(ROOT)
                deadline = time.monotonic() + 0.25
            journal.append(record)
            count += 1
            size += len(record)
            if count >= 256:
                flush()
        if len(buffer) > 65536:
            raise RuntimeError("oversized Apache log line")
        if deadline is not None and time.monotonic() >= deadline:
            flush()
    # Sealed backlog remains on NFS even if Apache terminates the logger.
    # No indefinite shutdown wait on an unavailable receiver.

if __name__ == "__main__":
    main()
