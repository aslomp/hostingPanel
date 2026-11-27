# Clean-server test installation

Experimental installer; live end-to-end testing is required. Start on a disposable Ubuntu 24.04 LTS, Ubuntu 26.04 LTS or Debian 12/13 host with a private network and an unused extra disk. CI checks scripts and package resolution on Ubuntu 24.04 and 26.04; a full installation with the actual host kernel, storage and network still requires live acceptance testing. ZFS packages must be available in the OS package repositories (Debian: contrib). Do not use a production host or a disk containing data.

## Default installation (no template)

    sudo bash installer/install.sh --full-server --new-cluster

The first full-server installation prompts for ACME email, primary and secondary authoritative nameserver FQDNs and panel FQDN. Configuration is persisted at /etc/hostingpanel/installer/cluster.json and reused. The default route determines the IPv4 address; storage access is limited to that node (/32). An existing imported ZFS pool named tank is reused. If no pool exists and exactly two whole disks are detected, with the root filesystem on one and the other unpartitioned, writable, non-removable, unsigned, unmounted and without holders, the installer proposes the unused disk. It shows its capacity and stable by-id path and requires typing its current device path before initialization. No proposal is made for ambiguous inventories or missing stable identifiers; explicitly configure the disk instead. Explicit templates never trigger interactive disk discovery. The panel hostname must resolve publicly to the ingress IP and port 80 must be reachable for ACME HTTP-01. Both nameservers may refer to this same server under two distinct DNS names pointing to the same IP address. This is not redundant DNS; separate servers are recommended for production and some registries require distinct addresses. Create the required DNS records and registrar glue where applicable. Automatic zone provisioning is enabled when both names are configured. Explicit templates may omit the panel hostname for private administration through an SSH tunnel and kubectl port-forward. The default ACME issuer uses production; explicit templates may select staging. Dedicated storage mode does not request ACME email; remote cluster/storage topology requires an explicit configuration with trusted node addresses. Workers can omit --config after receiving worker.json and agent-token in /etc/hostingpanel/installer; a join token and endpoint cannot be inferred. Only default local storage installation can ask for explicit disk confirmation.

## All-in-one with explicit configuration

    sudo install -d -m 0700 /root/hosting-cluster
    sudo cp installer/cluster.example.json /root/hosting-cluster/cluster.json
    sudo chmod 600 /root/hosting-cluster/cluster.json
    sudoedit /root/hosting-cluster/cluster.json

Set clusterHost and storageHost to the same private IPv4 address, storageNetwork to the trusted node subnet, webIp to the public ingress address (or private address for a lab), panelHostname, ns1/ns2 and acmeEmail to real values. Set poolDevices to stable /dev/disk/by-id paths for one empty disk or two empty mirrored disks and confirmPoolCreation=true, or create/import a pool beforehand. Default is no disk creation. Set localStorage=true. Default builds the panel image from the checked-out repository and imports it into K3s; for multiple nodes use a published image instead, with buildPanelImage=false and panelImage/panelTag pointing to a registry reachable from every node.

    sudo bash installer/install.sh --full-server --new-cluster --config /root/hosting-cluster/cluster.json

After an interrupted installation that created K3s, omit --new-cluster. Never uninstall K3s or destroy storage merely to retry. DNS must resolve the panel hostname to the ingress address; TCP 80/443 must be reachable for ACME. Staging is the default and does not provide a browser-trusted certificate. Set acmeStaging=false for production only after successful validation; separate ACME account Secrets are generated for staging and production.

## Separate storage and worker

Set localStorage=false and storageHost to the private storage address. Run --storage-server on storage first. Transfer cluster.json, csi-key and iscsi-chap securely to the root-only configuration directory on the control-plane machine. Then run --full-server --new-cluster there. The storage role does not install K3s. The control plane writes worker.json and agent-token; transfer only those two files to the worker's root-only configuration directory and run:

    sudo bash installer/install.sh --worker --config /root/hosting-cluster/worker.json

Both CSI controllers use host networking so SSH originates from the node address rather than the pod CIDR. The storageNetwork must authorize the actual source address of every node eligible to run a controller. Host networking is limited to these infrastructure controllers; it is not tenant isolation. The generic CSI controller uses a dedicated SSH key with root-equivalent sudo privileges. Treat this key as a storage administrator credential, not an ordinary account. Restrict SSH and all NFS/iSCSI traffic to trusted nodes on a private network. The installer adds an nftables rule for ports 111, 2049 and 3260; review existing firewall, RPC services, IPv6 and cloud firewall independently. iSCSI uses a generated CHAP secret shared by trusted cluster nodes; this is not per-tenant authentication. Do not expose storage publicly. CSI host-key pinning is not implemented yet.

## Verification

    sudo k3s kubectl get nodes
    sudo k3s kubectl get storageclass,pv
    sudo k3s kubectl -n hosting get pods,pvc,svc
    sudo k3s kubectl -n envoy-gateway-system get gateway
    sudo k3s kubectl get clusterissuer,certificate -A
    sudo k3s kubectl -n hosting get secret hostingpanel-bootstrap -o jsonpath='{.data.password}' | base64 -d; echo

Login username is admin. Keep credentials private. Verify HTTP and HTTPS, provisioning a test site, database creation and PVC persistence. The panel chart enables PowerDNS and MariaDB; mail/Stalwart is optional and not automatically enabled. Domain ACME provider mappings may need configuration in the panel. Storage classes retain volumes on claim deletion; review and clean up lab volumes explicitly.

The installer now gates success on dynamic NFS and iSCSI write/remount tests, certificate readiness, Gateway programming and the panel health endpoint. Failed storage checks retain the isolated diagnostic namespace. K3s secrets encryption is enabled for new clusters. This remains a test candidate until live acceptance passes. Runtime validation of ZFS, NFS permissions, iSCSI attach, Gateway API, certificate issuance and application provisioning is outstanding. For production require off-host backups and tested restore of etcd, ZFS datasets and application databases; verify isolation and network policies on the actual CNI. Local etcd snapshots are scheduled automatically. Encrypted off-host exports for etcd, ZFS and MariaDB are available; see [backup and recovery](RECOVERY.md). Off-host scheduling, coordinated checkpoints and verified recovery remain operator responsibilities.

## Automatic local ZFS snapshots

The storage installer enables three persistent systemd timers for POOL/hosting and its descendants. Schedules use UTC. Hourly snapshots retain the current and previous 71 hourly slots; daily snapshots retain the current and previous six calendar days; weekly snapshots run Sunday at 00:00 and retain a rolling calendar month (four or five Sundays). Expired snapshots are removed automatically on timer runs, only if they have the exact HostingPanel retention name and ownership marker. Manual, CSI and export snapshots are not selected. Holds and dependent clones block deletion; failures appear in the systemd journal. A missed run cannot reconstruct a historical snapshot. Local snapshots are crash-consistent and are not off-host backups. Inspect with:

    systemctl list-timers 'hosting-zfs-*'
    journalctl -u hosting-zfs-hourly.service
    zfs list -t snapshot -r tank/hosting
    sudo python3 /usr/local/lib/hostingpanel/zfs-retention.py tank/hosting hourly --dry-run

## Storage antivirus

Local storage and dedicated storage installations install ClamAV and enable FreshClam for automatic signature updates. The installer waits for the initial main and daily signature databases. The daily scan runs at 03:00 UTC (up to 15 minutes randomized delay) and scans the complete live /srv/hosting/home tree, including hidden files. It does not follow symlinks, enter .zfs snapshot directories or cross into other filesystems. The current installation stores all customer directories on one ZFS dataset; separately mounted customer datasets would need explicit scan coverage. Detected files are moved immediately into /var/lib/hostingpanel/quarantine (root-owned mode 0700, outside NFS exports). Move failures and scan errors require monitoring; quarantined regular files are permanently removed after at least 30 days based on inode change time (not the original file modification time). The persistent cleanup timer runs daily at 04:00 UTC and skips symlinks, directories and special files. Metadata changes can extend retention. Cleanup never traverses customer directories. ClamAV daemon and clamonacc run continuously, with prevention and extra scanning enabled. Monitored permission events deny access on scan errors. This is not a guarantee of NFS-wide prevention: kernel NFS server events, writes to open files and client caching must be tested from actual workers. A file can be written or read before an asynchronous detection; the daily scan remains a fallback. Scan size, archive and other engine limits still apply. The standalone daily scanner loads signatures once per scan.

    sudo clam-on-demand
    sudo systemctl start hosting-clamav-scan.service
    systemctl list-timers hosting-clamav-scan.timer
    sudo journalctl -u hosting-clamav-scan.service
    sudo journalctl -u clamav-freshclam.service
    sudo systemctl status hosting-clamav-onaccess.service
    sudo journalctl -u hosting-clamav-onaccess.service
    systemctl list-timers hosting-clamav-quarantine-cleanup.timer
    sudo python3 /usr/local/lib/hostingpanel/quarantine-cleanup.py --dry-run
    sudo journalctl -u hosting-clamav-quarantine-cleanup.service

Verify a harmless EICAR test file in an isolated test account before production and verify the file disappears from the live export and appears in quarantine. Repeat from an actual NFS worker with a new file, overwrite, rename and read; confirm detection and measure time to isolation. Verify that clean file access still works, including during scanner restart. Do not mark the deployment production-ready if these tests fail. Never use live malware to test the scanner. Configure monitoring for failed scan services and outdated signature updates.
