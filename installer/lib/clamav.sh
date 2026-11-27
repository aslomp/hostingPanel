install_clamav() {
  apt-get install -y clamav clamav-freshclam clamav-daemon clamdscan
  install -d -m 0755 /usr/local/lib/hostingpanel
  install -d -m 0700 -o root -g root /var/lib/hostingpanel/quarantine
  install -m 0755 "$here/clam-on-demand.sh" /usr/local/lib/hostingpanel/clam-on-demand.sh
  ln -sfn /usr/local/lib/hostingpanel/clam-on-demand.sh /usr/local/sbin/clam-on-demand
  systemctl enable --now clamav-freshclam.service
  # FreshClam may need time to retrieve the initial database.
  local attempt
  for ((attempt=0; attempt<120; attempt++)); do
    if compgen -G '/var/lib/clamav/main.c[lv]d' >/dev/null &&
       compgen -G '/var/lib/clamav/daily.c[lv]d' >/dev/null; then break; fi
    sleep 5
  done
  ((attempt<120)) || die "ClamAV signature database unavailable; inspect clamav-freshclam logs"
  systemctl enable --now clamav-daemon.service
  for ((attempt=0; attempt<60; attempt++)); do
    if clamdscan --ping 1 >/dev/null 2>&1; then break; fi
    sleep 5
  done
  ((attempt<60)) || die "ClamAV daemon not ready"
  command -v clamonacc >/dev/null || die "ClamAV on-access client missing"
  local socket
  socket=$(awk '$1 == "LocalSocket" {print $2}' /etc/clamav/clamd.conf)
  [[ "$socket" == /* && -S "$socket" ]] || die "Local clamd socket required"
  cat > /etc/clamav/hosting-onaccess.conf <<EOF
LocalSocket $socket
OnAccessIncludePath /srv/hosting/home
OnAccessExcludePath /srv/hosting/home/.zfs
OnAccessExcludeUname clamav
OnAccessPrevention yes
OnAccessExtraScanning yes
EOF
  cat > /etc/systemd/system/hosting-clamav-onaccess.service <<'EOF'
[Unit]
Description=HostingPanel realtime malware protection
After=clamav-daemon.service zfs-mount.service
Requires=clamav-daemon.service
RequiresMountsFor=/srv/hosting/home
[Service]
Type=simple
ExecStart=/usr/sbin/clamonacc --foreground --fdpass --config-file=/etc/clamav/hosting-onaccess.conf --move=/var/lib/hostingpanel/quarantine
Restart=on-failure
RestartSec=5s
UMask=0077
[Install]
WantedBy=multi-user.target
EOF
  cat > /etc/systemd/system/hosting-clamav-scan.service <<'EOF'
[Unit]
Description=HostingPanel on-demand scan of all hosting account directories
After=clamav-freshclam.service zfs-mount.service
Wants=clamav-freshclam.service
RequiresMountsFor=/srv/hosting/home
[Service]
Type=oneshot
ExecStart=/usr/local/sbin/clam-on-demand
Nice=10
IOSchedulingClass=idle
UMask=0077
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true
ReadWritePaths=/run/lock /srv/hosting/home /var/lib/hostingpanel/quarantine
TimeoutStartSec=12h
EOF
  cat > /etc/systemd/system/hosting-clamav-scan.timer <<'EOF'
[Unit]
Description=Daily HostingPanel malware scan
[Timer]
OnCalendar=*-*-* 03:00:00 UTC
Persistent=true
RandomizedDelaySec=15m
[Install]
WantedBy=timers.target
EOF
  systemctl daemon-reload
  systemctl enable --now hosting-clamav-onaccess.service
  systemctl restart hosting-clamav-onaccess.service
  systemctl is-active --quiet hosting-clamav-onaccess.service
  systemctl enable --now hosting-clamav-scan.timer
}
