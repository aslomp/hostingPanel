install_storage() {
  local pool host network
  pool=$(require pool); host=$(require storageHost); network=$(require storageNetwork)
  [[ "$pool" =~ ^[a-zA-Z][a-zA-Z0-9_-]*$ ]] || die "Invalid pool"
  python3 -c 'import ipaddress,sys; ipaddress.ip_network(sys.argv[1]); ipaddress.ip_address(sys.argv[2])' "$network" "$host"
  apt-get update
  apt-get install -y zfsutils-linux nfs-kernel-server targetcli-fb openssh-server sudo
  if ! zpool list -H "$pool" >/dev/null 2>&1; then
    [[ $(get confirmPoolCreation) == True ]] || die "Create/import pool or explicitly confirm pool creation"
    local devices
    devices=$(python3 -c 'import json,sys; print("\n".join(json.load(open(sys.argv[1]))["poolDevices"]))' "$config")
    [[ -n "$devices" ]] || die "Specify unused disk by stable /dev/disk/by-id path"
    local -a disks=()
    while IFS= read -r disk; do
      [[ "$disk" == /dev/disk/by-id/* && -b "$disk" ]] || die "Invalid disk"
      [[ $(lsblk -nr -o TYPE "$disk") == disk ]] || die "Disk has partitions or children"
      [[ -z $(wipefs -n --noheadings -o TYPE "$disk") ]] || die "Disk has signatures"
      disks+=("$disk")
    done <<< "$devices"
    if ((${#disks[@]} == 1)); then zpool create "$pool" "${disks[@]}";
    elif ((${#disks[@]} == 2)); then zpool create "$pool" mirror "${disks[@]}";
    else die "Use one disk or two mirrored disks"; fi
  fi
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
  local base
  base=$(dirname "$config")
  [[ -f "$base/csi-key" ]] || ssh-keygen -q -t rsa -b 3072 -m PEM -N '' -f "$base/csi-key"
  id hosting-csi >/dev/null 2>&1 || useradd -m -s /bin/bash hosting-csi
  install -d -m 0700 -o hosting-csi -g hosting-csi /home/hosting-csi/.ssh
  printf 'restrict,from="%s" %s\n' "$network" "$(ssh-keygen -y -f "$base/csi-key")" > /home/hosting-csi/.ssh/authorized_keys
  chown hosting-csi:hosting-csi /home/hosting-csi/.ssh/authorized_keys
  chmod 600 /home/hosting-csi/.ssh/authorized_keys
  printf 'hosting-csi ALL=(root) NOPASSWD: ALL\n' > /etc/sudoers.d/hosting-csi
  chmod 0440 /etc/sudoers.d/hosting-csi
  visudo -cf /etc/sudoers.d/hosting-csi
  apt-get install -y nftables
  install -d /etc/hostingpanel
  cat > /etc/hostingpanel/storage-firewall.nft <<EOF
table inet hosting_storage {
 chain input {
  type filter hook input priority -10; policy accept;
  iifname "lo" accept
  ip saddr $network accept
  tcp dport { 111, 2049, 3260 } drop
  udp dport { 111, 2049, 3260 } drop
 }
}
EOF
  cat > /etc/systemd/system/hosting-storage-firewall.service <<'EOF'
[Unit]
Description=HostingPanel storage ingress restriction
Before=nfs-kernel-server.service
[Service]
Type=oneshot
ExecStartPre=-/usr/sbin/nft delete table inet hosting_storage
ExecStart=/usr/sbin/nft -f /etc/hostingpanel/storage-firewall.nft
RemainAfterExit=yes
[Install]
WantedBy=multi-user.target
EOF
  systemctl daemon-reload
  systemctl enable hosting-storage-firewall
  systemctl restart hosting-storage-firewall
  systemctl enable --now nfs-kernel-server ssh
  exportfs -ra
  python3 "$here/generate.py" "$config" generate
  targetcli saveconfig
  systemctl enable rtslib-fb-targetctl.service || die "Cannot enable persistent iSCSI targets"
  echo "Storage ready: $host:/srv/hosting/home and $host:/srv/hosting/apache"
}
