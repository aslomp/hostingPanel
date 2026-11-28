# 0002 — Native OpenSSH session identities

Status: accepted implementation direction; runtime acceptance pending.

Use one shared OpenSSH listener rather than SFTPGo. SFTPGo local filesystem operations run under the service identity and assign ownership with chown; failure may only be logged. This conflicts with root-squashed NFS and native UID-based ZFS quotas. OpenSSH uses authenticated Unix identities and kernel chroot, dropping session privileges. Keep existing API, SQLite and outbox. Kubernetes creates per-login subPath mounts into local root-owned jails; no in-pod mount capability or LDAP. Initial implementation uses a Recreate Deployment and immutable snapshot; account changes disconnect all active sessions. A live mount mechanism is deliberately not introduced until its privilege and concurrency model is justified. No claim of production readiness until actual Kubernetes/NFS tests pass.
