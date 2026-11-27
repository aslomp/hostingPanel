# Clean-server test installation

Experimental installer; live end-to-end testing is required. Start on a disposable Ubuntu 24.04 LTS or Debian host with a private network and an unused extra disk. ZFS packages must be available in the OS package repositories (Debian: contrib). Do not use a production host or a disk containing data.

## All-in-one

    sudo install -d -m 0700 /root/hosting-cluster
    sudo cp installer/cluster.example.json /root/hosting-cluster/cluster.json
    sudo chmod 600 /root/hosting-cluster/cluster.json
    sudoedit /root/hosting-cluster/cluster.json

Set clusterHost and storageHost to the same private IPv4 address, storageNetwork to the trusted node subnet, webIp to the public ingress address (or private address for a lab), panelHostname, ns1/ns2 and acmeEmail to real values. Set poolDevices to stable /dev/disk/by-id paths for one empty disk or two empty mirrored disks and confirmPoolCreation=true, or create/import a pool beforehand. Default is no disk creation. Set localStorage=true. Default builds the panel image from the checked-out repository and imports it into K3s; for multiple nodes use a published image instead, with buildPanelImage=false and panelImage/panelTag pointing to a registry reachable from every node.

    sudo bash installer/install.sh --full-server --new-cluster --config /root/hosting-cluster/cluster.json

After an interrupted installation that created K3s, omit --new-cluster. Never uninstall K3s or destroy storage merely to retry. DNS must resolve the panel hostname to the ingress address; TCP 80/443 must be reachable for ACME. Staging is the default and does not provide a browser-trusted certificate. Set acmeStaging=false for production only after successful validation; use a separate ACME account Secret when switching environments (delete the old issuer account Secret only after confirming the change, never the TLS Secret).

## Separate storage and worker

Set localStorage=false and storageHost to the private storage address. Run --storage-server on storage first. Transfer cluster.json and csi-key securely to the root-only configuration directory on the control-plane machine. Then run --full-server --new-cluster there. The storage role does not install K3s. The control plane writes worker.json and agent-token; transfer only those two files to the worker's root-only configuration directory and run:

    sudo bash installer/install.sh --worker --config /root/hosting-cluster/worker.json

The generic CSI controller uses a dedicated SSH key with root-equivalent sudo privileges. Treat this key as a storage administrator credential, not an ordinary account. Restrict SSH and all NFS/iSCSI traffic to trusted nodes on a private network. The installer adds an nftables rule for ports 111, 2049 and 3260; review existing firewall, RPC services, IPv6 and cloud firewall independently. iSCSI does not use CHAP in this initial lab profile. Do not expose storage publicly. CSI host-key pinning is not implemented yet.

## Verification

    sudo k3s kubectl get nodes
    sudo k3s kubectl get storageclass,pv
    sudo k3s kubectl -n hosting get pods,pvc,svc
    sudo k3s kubectl -n envoy-gateway-system get gateway
    sudo k3s kubectl get clusterissuer,certificate -A
    sudo k3s kubectl -n hosting get secret hostingpanel-bootstrap -o jsonpath='{.data.password}' | base64 -d; echo

Login username is admin. Keep credentials private. Verify HTTP and HTTPS, provisioning a test site, database creation and PVC persistence. The panel chart enables PowerDNS and MariaDB; mail/Stalwart is optional and not automatically enabled. Domain ACME provider mappings may need configuration in the panel. Storage classes retain volumes on claim deletion; review and clean up lab volumes explicitly.

This is a test candidate, not a production-ready deployment. Runtime validation of ZFS, NFS permissions, iSCSI attach, Gateway API, certificate issuance and application provisioning is outstanding.
