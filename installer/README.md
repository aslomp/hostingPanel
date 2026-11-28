# Clean-server test installation

Experimental installer; live end-to-end testing is required. Start on a disposable Ubuntu 24.04 LTS, Ubuntu 26.04 LTS or Debian 12/13 host with a private network and an unused extra disk. CI checks scripts and package resolution on Ubuntu 24.04 and 26.04; a full installation with the actual host kernel, storage and network still requires live acceptance testing. ZFS packages must be available in the OS package repositories (Debian: contrib). Do not use a production host or a disk containing data.

## Default installation (no template)

    sudo bash installer/install.sh --full-server --new-cluster

The first full-server installation prompts for ACME email, primary and secondary authoritative nameserver FQDNs and panel FQDN. Configuration is persisted at /etc/hostingpanel/installer/cluster.json and reused. The default route determines the IPv4 address; storage access is limited to that node (/32). An existing imported ZFS pool named tank is reused. If no pool exists and exactly two whole disks are detected, with the root filesystem on one and the other unpartitioned, writable, non-removable, unsigned, unmounted and without holders, the installer proposes the unused disk. It shows its capacity and stable by-id path and requires typing its current device path before initialization. No proposal is made for ambiguous inventories or missing stable identifiers; explicitly configure the disk instead. Explicit templates never trigger interactive disk discovery. Initial panel TLS uses a self-signed certificate and does not require public DNS. For later ACME HTTP-01 issuance, the panel hostname must resolve publicly to ingress and port 80 must be reachable. Both nameservers may refer to this same server under two distinct DNS names pointing to the same IP address. This is not redundant DNS; separate servers are recommended for production and some registries require distinct addresses. Create the required DNS records and registrar glue where applicable. Automatic zone provisioning is enabled when both names are configured. Explicit templates may omit the panel hostname for private administration through an SSH tunnel and kubectl port-forward. The default ACME issuer uses production; explicit templates may select staging. Dedicated storage mode does not request ACME email; remote cluster/storage topology requires an explicit configuration with trusted node addresses. Workers can omit --config after receiving worker.json and agent-token in /etc/hostingpanel/installer; a join token and endpoint cannot be inferred. Only default local storage installation can ask for explicit disk confirmation.

## All-in-one with explicit configuration

    sudo install -d -m 0700 /root/hosting-cluster
    sudo cp installer/cluster.example.json /root/hosting-cluster/cluster.json
    sudo chmod 600 /root/hosting-cluster/cluster.json
    sudoedit /root/hosting-cluster/cluster.json

Set clusterHost and storageHost to the same private IPv4 address, storageNetwork to the trusted node subnet, webIp to the public ingress address (or private address for a lab), panelHostname, ns1/ns2 and acmeEmail to real values. Set poolDevices to stable /dev/disk/by-id paths for one empty disk or two empty mirrored disks and confirmPoolCreation=true, or create/import a pool beforehand. Default is no disk creation. Set localStorage=true. Default builds the panel image from the checked-out repository and imports it into K3s; for multiple nodes use a published image instead, with buildPanelImage=false and panelImage/panelTag pointing to a registry reachable from every node.

    sudo bash installer/install.sh --full-server --new-cluster --config /root/hosting-cluster/cluster.json

After an interrupted installation that created K3s, omit --new-cluster. Never uninstall K3s or destroy storage merely to retry. Public DNS and TCP 80/443 are needed for later public ACME issuance, not bootstrap. Staging is the default and does not provide a browser-trusted certificate. Set acmeStaging=false for production only after successful validation; separate ACME account Secrets are generated for staging and production.

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

## Promote the panel certificate to Let's Encrypt

After public DNS and ingress are ready, change the existing Certificate issuer (the Gateway continues to reference the same TLS Secret):

    sudo k3s kubectl -n envoy-gateway-system patch certificate hosting-panel-tls --type=merge -p '{"spec":{"issuerRef":{"name":"lets-encrypt","kind":"ClusterIssuer","group":"cert-manager.io"}}}'
    sudo k3s kubectl -n envoy-gateway-system describe certificate hosting-panel-tls

The bootstrap certificate is not browser-trusted. Use a trusted local DNS/hosts mapping for access; verify the certificate before sending credentials. The installer probes the configured ingress IP with the hostname and pins the public key from the Kubernetes TLS Secret. It does not depend on public DNS. Existing TLS Secrets are preserved on reruns. If a prior installation has an unissued ACME Certificate without a TLS Secret, the installer switches it to bootstrap issuance. Verify the new issuer and served certificate after promotion; Ready alone may reflect an older certificate during renewal.

## Clean-host capacity and DNS exposure

Before installing packages, the installer checks available root filesystem space (full server 25 GiB, storage server 5 GiB, worker 10 GiB) and nominal RAM (8, 4 and 2 GiB respectively, allowing 10% reserved memory). These are conservative installation budgets, not workload sizing guarantees. On insufficient root space it reports LVM free extents when available; it never resizes filesystems automatically. The installer configures the authoritative DNS Service as ClusterIP with externalIPs restricted to the configured node-owned clusterHost address, avoiding ServiceLB hostPort 53 interception of the local resolver. Both TCP and UDP are exposed on that address; verify DNS from another host and verify node recursive DNS after deployment. Existing chart defaults remain unchanged for separately managed installations. Helm readiness remains mandatory; the installer prints pod/PVC status and recent warnings during the wait and reports diagnostics on failure. A clean Git revision with a matching recorded imported image can be reused on retry; modified source is rebuilt.

## Temporary Ubuntu 26.04 AppArmor compatibility

Only Ubuntu 26.04 with kernel `7.0.0-44-generic` and K3s `v1.35.10+k3s1` (containerd `v2.2.10-k3s1`) receives the version-pinned profile backport for [containerd #14485](https://github.com/containerd/containerd/issues/14485) and [#14487](https://github.com/containerd/containerd/pull/14487). The rootful default profile preserves baseline deny rules and adds only the exact stacked unconfined peer for signal and ptrace; AppArmor stays enforcing and Ubuntu sysctls remain untouched. Existing custom policy requires manual review. A K3s service pre-start hook reapplies the compatibility only on the exact tuple; it is not installed as a general boot-loaded AppArmor policy. On a different kernel or K3s release the hook does nothing; after upgrade reboot to clear the in-memory compatibility profile, then remove the installer-owned hook and helper/profile files once the upstream runtime is verified. For the existing affected installation, an administrator can load the vendored profile directly with `sudo apparmor_parser -r -K installer/compat/containerd-2.2.10-k3s1.apparmor` after confirming the tuple and absence of custom policy. The installer also runs an isolated non-root signal smoke test before storage and application setup. Live validation is required; shell and policy compilation checks are not equivalent to exercising kernel signal mediation.

## Panel DNS bootstrap and round trip

After successful HTTPS health check the full-server installer authenticates with the initial Kubernetes bootstrap administrator Secret over the certificate-key-pinned external Gateway, calls official legacy CMD_API_DNS_ADMIN exists/create/add, and uses the existing typed DNS inventory only for conflict detection and idempotent read-back. It never writes directly to PowerDNS or SQLite. By default the three explicitly configured panel and nameserver hostnames must share their immediate parent (e.g. panel.testhost.xyz, ns1.testhost.xyz, ns2.testhost.xyz); this authorizes creation of that parent zone. With different parents, explicitly specify panelDnsZone in cluster.json. Only in-zone nameserver addresses are added. This assumes both in-zone nameservers serve the configured webIp; public delegation/glue remains external. Existing conflicting records are rejected, not replaced. The installer waits for exact authoritative NS and A answers over UDP and TCP on clusterHost before printing login instructions. The bootstrap password is never printed automatically; if it has been changed, use the normal authenticated panel workflow rather than resetting it. For an already deployed updated image, rerun only this step with sudo python3 installer/bootstrap-dns.py /etc/hostingpanel/installer/cluster.json (dnsutils required). Live deployment acceptance remains necessary.

## Local image build runtime

Docker is a build-time dependency only. On a clean host without existing Docker or standalone containerd packages or service units, the installer records ownership in the root-only configuration directory (installer-owned-docker), builds and imports the image into K3s, then stops and disables docker.socket, docker.service and containerd.service, including on build failure or ordinary interruption. A cached image does not start Docker; an owned runtime left active by an interrupted build is stopped on cache reuse. Packages and image cache remain for future upgrades. K3s and its embedded containerd are not stopped. Existing installations without the ownership marker are not automatically stopped or disabled: review their use before adopting or manually disabling them. Do not repurpose an installer-owned Docker runtime for independent workloads without removing the ownership marker and managing the services yourself. Snapd, antivirus and storage services are not changed.

## Optional durable HTTP accounting

First bootstrap the panel with accountingSpoolEnabled=false. After the panel HTTPS origin is trusted, set accountingSpoolEnabled=true, accountingEndpoint to that HTTPS origin, accountingImage to a tagged Apache accounting image, and buildAccountingImage=true for a local single-node build (false for a registry image already available to all nodes). Rerun the storage role first for separate storage, then the full-server installer without --new-cluster. The storage role creates POOL/hosting/accounting at /srv/hosting/accounting with sync=always, mode 0700, UID/GID 1001 and a restricted root-squashed NFS export. The control-plane installer checks HTTPS health without disabling certificate validation, creates/reuses the dedicated token Secret, builds/imports Dockerfile.apache-accounting when requested, and generates the Helm values. Existing installations are not silently switched. Multi-node installations require image distribution to all eligible nodes. A self-signed bootstrap certificate is insufficient without explicit trusted CA configuration. Existing spool files and export are preserved when disabled; do not delete the dataset to retry. Live NFS crash recovery, in-pod HTTPS reachability and quota enforcement remain unverified.

## Stalwart mail

The canonical Kubernetes chart enables Stalwart by default. Clean-server installation supplies `zfs-iscsi` for its separate persistent configuration (1 GiB) and data (20 GiB) volumes. The chart exposes its existing mail protocol Service; the administration/bootstrap Service remains ClusterIP and public HTTPS is disabled. Initial Stalwart setup, trusted mail TLS, DNS/MX and a dedicated management API key still require configuration. The installer does not invent an API credential or enable the panel mail API before that credential and trusted HTTPS origin exist. For a manual Helm deployment, set `stalwart.enabled=false` to opt out.
