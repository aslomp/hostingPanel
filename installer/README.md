# Cluster installer (initial scaffold)

Run as root on Debian/Ubuntu with Python 3. Copy cluster.example.json to a private configuration directory and choose tested release versions. Create or import the ZFS pool explicitly; the installer never formats disks. For local storage set localStorage=true and storageHost to the server's private address.

- bash installer/install.sh --storage-server --config /root/cluster/cluster.json
- bash installer/install.sh --full-server --new-cluster --config /root/cluster/cluster.json
- bash installer/install.sh --worker --config /root/cluster/cluster.json

The server exports agent-token beside the config; copy only the JSON and agent-token securely to workers. Do not copy CSI credentials or application secrets to workers.

This initial implementation requires operator-provided Helm, csi-nfs.yaml, csi-iscsi.yaml, gateways.yaml, issuer.yaml and hosting-values.yaml alongside cluster.json. Configure a dedicated CSI SSH identity, verified host key, restricted storage network and LIO persistence. The issuer must be named lets-encrypt and use the external HTTP gateway. Use the existing chart schema and set nfsServer, nfsPath=/srv/hosting/home, apacheConfigPath=/srv/hosting/apache, gateway settings, bootstrap secret and iSCSI storage classes. See docs/kubernetes-hosting.md. Verify the provisioning UID/GID against the deployed chart before use.

This is not yet a turnkey production installer: credential generation, CSI configuration, gateway/issuer generation, firewall enforcement, bootstrap secrets, version validation and live deployment tests remain outstanding.
