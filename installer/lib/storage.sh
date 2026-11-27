install_storage() {
  local pool host network
  pool=$(require pool); host=$(require storageHost); network=$(require storageNetwork)
  [[ "$pool" =~ ^[a-zA-Z][a-zA-Z0-9_-]*$ ]] || die "Invalid pool"
  python3 -c 'import ipaddress,sys; ipaddress.ip_network(sys.argv[1]); ipaddress.ip_address(sys.argv[2])' "$network" "$host"
  apt-get update
  apt-get install -y zfsutils-linux nfs-kernel-server targetcli-fb openssh-server sudo
  zpool list -H "$pool" >/dev/null || die "Create the ZFS pool explicitly before running installer"
  for dataset in hosting hosting/home hosting/apache hosting/volumes hosting/snapshots hosting/nfs-volumes hosting/nfs-snapshots; do
    zfs list -H "$pool/$dataset" >/dev/null 2>&1 || zfs create "$pool/$dataset"
  done
  for dataset in home apache; do
    zfs set "mountpoint=/srv/hosting/$dataset" "$pool/hosting/$dataset"
  done
  chown 1654:1654 /srv/hosting/home /srv/hosting/apache
  chmod 0755 /srv/hosting/home /srv/hosting/apache
  install -d /etc/exports.d
  printf '/srv/hosting/home %s(rw,sync,root_squash,no_subtree_check)\n/srv/hosting/apache %s(rw,sync,root_squash,no_subtree_check)\n' "$network" "$network" > /etc/exports.d/hostingpanel.exports
  systemctl enable --now nfs-kernel-server ssh
  exportfs -ra
  # The CSI controller uses an explicitly supplied, separately authorized SSH identity.
  targetcli saveconfig
  echo "Storage ready: $host:/srv/hosting/home and $host:/srv/hosting/apache"
}
