install_storage() {
  local pool host network
  pool=$(require pool); host=$(require storageHost); network=$(require storageNetwork)
  [[ "$pool" =~ ^[a-zA-Z][a-zA-Z0-9_-]*$ ]] || die "Invalid pool"
  python3 -c 'import ipaddress,sys; ipaddress.ip_network(sys.argv[1]); ipaddress.ip_address(sys.argv[2])' "$network" "$host"
  apt-get update
  apt-get install -y zfsutils-linux nfs-kernel-server targetcli-fb openssh-server sudo openssl
  if ! zpool list -H "$pool" >/dev/null 2>&1; then
    if [[ "$default_config" == true && $(get confirmPoolCreation) == False ]]; then
      python3 "$here/disk_discovery.py" "$config"
    fi
    [[ $(get confirmPoolCreation) == True ]] || die "Create/import pool or explicitly confirm pool creation"
    local devices
    devices=$(python3 -c 'import json,sys; print("\n".join(json.load(open(sys.argv[1]))["poolDevices"]))' "$config")
    [[ -n "$devices" ]] || die "Specify unused disk by stable /dev/disk/by-id path"
    local -a disks=()
    while IFS= read -r disk; do
      [[ "$disk" == /dev/disk/by-id/* && -b "$disk" ]] || die "Invalid disk"
      [[ $(lsblk -nr -o TYPE "$disk") == disk ]] || die "Disk has partitions or children"
      local signatures
      signatures=$(wipefs --no-act --noheadings --output TYPE "$disk") || die "Cannot inspect disk signatures"
      [[ -z "$signatures" ]] || die "Disk has signatures"
      local mounts
      mounts=$(lsblk -nr -o MOUNTPOINTS "$disk" | tr -d '[:space:]') || die "Cannot inspect disk mounts"
      [[ -z "$mounts" ]] || die "Disk is mounted"
      [[ $(lsblk -dn -o RO,RM "$disk" | tr -d '[:space:]') == 00 ]] || die "Disk is read-only or removable"
      [[ -z $(ls -A "/sys/class/block/$(basename "$(readlink -f "$disk")")/holders") ]] || die "Disk has active holders"
      for previous in "${disks[@]}"; do
        [[ $(readlink -f "$previous") != $(readlink -f "$disk") ]] || die "Duplicate disk alias"
      done
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
    local current
    current=$(zfs get -H -o value mountpoint "$pool/hosting/$dataset")
    if [[ "$current" != "/srv/hosting/$dataset" ]]; then
      [[ "$current" == "/$pool/hosting/$dataset" ]] || die "Unexpected existing mountpoint: $current"
      zfs set "mountpoint=/srv/hosting/$dataset" "$pool/hosting/$dataset"
    fi
    mountpoint -q "/srv/hosting/$dataset" || die "Dataset is not mounted"
  done
  chown 1654:1654 /srv/hosting/home /srv/hosting/apache
  chmod 0755 /srv/hosting/home /srv/hosting/apache
  if [[ $(get accountingSpoolEnabled) == True ]]; then
    zfs list -H "$pool/hosting/accounting" >/dev/null 2>&1 || zfs create -o mountpoint=/srv/hosting/accounting -o sync=always "$pool/hosting/accounting"
    [[ $(zfs get -H -o value mountpoint "$pool/hosting/accounting") == /srv/hosting/accounting ]] || die "Unexpected accounting mountpoint"
    mountpoint -q /srv/hosting/accounting || die "Accounting dataset not mounted"
    [[ $(zfs get -H -o value sync "$pool/hosting/accounting") == always ]] || die "Accounting dataset requires sync=always"
    chown 1001:1001 /srv/hosting/accounting
    chmod 0700 /srv/hosting/accounting
  fi
  zfs list -H "$pool/hosting/sftp-jails" >/dev/null 2>&1 || zfs create -o mountpoint=/srv/hosting/sftp-jails "$pool/hosting/sftp-jails"
  [[ $(zfs get -H -o value mountpoint "$pool/hosting/sftp-jails") == /srv/hosting/sftp-jails ]] || die "Unexpected SFTP jail mountpoint"
  mountpoint -q /srv/hosting/sftp-jails || die "SFTP jail dataset not mounted"
  chown root:root /srv/hosting/sftp-jails
  chmod 0755 /srv/hosting/sftp-jails
  install -d /etc/exports.d
  printf '/srv/hosting/home %s(rw,sync,root_squash,no_subtree_check)\n/srv/hosting/apache %s(rw,sync,root_squash,no_subtree_check)\n' "$network" "$network" > /etc/exports.d/hostingpanel.exports
  printf '/srv/hosting/sftp-jails %s(rw,sync,root_squash,no_subtree_check)\n' "$network" >> /etc/exports.d/hostingpanel.exports
  # Preserve an existing accounting export on reruns, even when ingestion is disabled.
  if zfs list -H "$pool/hosting/accounting" >/dev/null 2>&1; then
    [[ $(zfs get -H -o value mountpoint "$pool/hosting/accounting") == /srv/hosting/accounting ]] || die "Unexpected accounting mountpoint"
    printf '/srv/hosting/accounting %s(rw,sync,root_squash,no_subtree_check)\n' "$network" >> /etc/exports.d/hostingpanel.exports
  fi
  local base
  base=$(dirname "$config")
  [[ -f "$base/iscsi-chap" ]] || openssl rand -hex 8 > "$base/iscsi-chap"
  [[ -f "$base/csi-key" ]] || ssh-keygen -q -t rsa -b 3072 -m PEM -N '' -f "$base/csi-key"
  id hosting-csi >/dev/null 2>&1 || useradd -m -s /bin/bash hosting-csi
  install -d -m 0700 -o hosting-csi -g hosting-csi /home/hosting-csi/.ssh
  # targetcli requires an SSH pseudo-terminal; retain all other key restrictions.
  printf 'restrict,pty,from="%s" %s\n' "$network" "$(ssh-keygen -y -f "$base/csi-key")" > /home/hosting-csi/.ssh/authorized_keys
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
  # The control plane generates Helm inputs after receiving storage credentials.
  if [[ -n $(get acmeEmail) ]]; then python3 "$here/generate.py" "$config" generate; fi
  targetcli saveconfig
  systemctl enable rtslib-fb-targetctl.service || die "Cannot enable persistent iSCSI targets"
  install_snapshot_retention "$pool"
  install_clamav
  echo "Storage ready: $host:/srv/hosting/home and $host:/srv/hosting/apache"
}

install_snapshot_retention() {
  local pool=$1 tier schedule
  install -d -m 0755 /usr/local/lib/hostingpanel
  install -m 0755 "$here/zfs-retention.py" /usr/local/lib/hostingpanel/zfs-retention.py
  for tier in hourly daily weekly; do
    case "$tier" in
      hourly) schedule="*-*-* *:00:00 UTC" ;;
      daily) schedule="*-*-* 00:00:00 UTC" ;;
      weekly) schedule="Sun *-*-* 00:00:00 UTC" ;;
    esac
    cat > "/etc/systemd/system/hosting-zfs-$tier.service" <<EOF
[Unit]
Description=HostingPanel $tier ZFS snapshots and retention
After=zfs-mount.service
Requires=zfs-mount.service
[Service]
Type=oneshot
ExecStart=/usr/bin/python3 /usr/local/lib/hostingpanel/zfs-retention.py $pool/hosting $tier
UMask=0077
EOF
    cat > "/etc/systemd/system/hosting-zfs-$tier.timer" <<EOF
[Unit]
Description=HostingPanel $tier ZFS snapshot schedule
[Timer]
OnCalendar=$schedule
Persistent=true
AccuracySec=1s
[Install]
WantedBy=timers.target
EOF
  done
  systemctl daemon-reload
  for tier in hourly daily weekly; do
    systemctl enable --now "hosting-zfs-$tier.timer"
  done
  systemctl start hosting-zfs-hourly.service hosting-zfs-daily.service
  if [[ $(date -u +%u) == 7 ]]; then
    systemctl start hosting-zfs-weekly.service
  fi
}
