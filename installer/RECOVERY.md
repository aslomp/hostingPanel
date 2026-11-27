# Backup and disaster recovery

A backup is not considered verified until restored on an isolated host. Keep the age private identity offline, separate from the backup destination. Install age on each backup host. Use a mounted, independently administered off-host destination; never rely on the system disk or source pool. Apply retention at the destination, not by automatically deleting source datasets.

## Export

On the control-plane server:

    sudo bash installer/backup-control-plane.sh /mnt/backup age1...
    sudo bash installer/backup-mariadb.sh /mnt/backup age1...

On the storage server:

    sudo bash installer/backup-zfs.sh tank /mnt/backup age1...

Also encrypt and export the root-only installer configuration directory (including CSI key and CHAP secret), storage host SSH keys, NFS exports, targetcli configuration, and K3s configuration. Keep a versioned copy of the exact source revision and container images. These three scripts are independent exports, not a coordinated application-consistent checkpoint. Quiesce application writes and schema changes for a coordinated recovery point. Logical MariaDB export does not guarantee transactional consistency for non-InnoDB tables.

## Restore rehearsal

1. Provision an isolated replacement server with matching K3s and ZFS versions. Never run reset or receive commands against the original production host.
2. Decrypt the control-plane archive into a private directory. Use the original server token and the K3s documented etcd snapshot restore process (cluster-reset with cluster-reset-restore-path). Do not join the replacement to the live cluster.
3. Decrypt the ZFS stream and inspect with zfs receive -n before receiving into a new isolated pool. Do not use receive -F. Preserve CSI dataset names and map the original storage endpoint deliberately. Restore exports and LIO target configuration; verify target IQNs and PV volume handles.
4. Restore MariaDB logical dump to a disposable database instance of the same version; verify users, grants, tables, routines and representative row counts. Do not blindly import system tables into a different version.
5. Verify PVC bindings, mounts, panel login, website content, DNS, database access and certificates before routing traffic. Test reboot and worker replacement.
6. Record recovery time and recovery point, and retain the original encrypted backups.

No automatic destructive restore is provided. Restore procedures must be validated on the actual topology before production use.
