# 0002 — Native OpenSSH session identities

Status: accepted direction; runtime acceptance pending.

Use one shared OpenSSH listener rather than SFTPGo. Native UID/GID sessions and standard kernel chroot support NFS root squash and native ZFS quotas. Keep API, SQLite and outbox. Unix identities and public keys are projected through a fixed ConfigMap; no per-account pod spec changes, reload or restart. The network-facing server mounts one permanent root-owned jail export, without mount capabilities. Existing customer-owned DirectAdmin paths are not valid chroot ancestors; prepare root-owned jails on the storage host without changing customer data ownership. Automatic jail preparation and selective revocation of active sessions are still outstanding. Do not deploy in production until runtime acceptance passes.
