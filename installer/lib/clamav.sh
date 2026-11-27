install_clamav() {
  apt-get install -y clamav clamav-freshclam
  install -d -m 0755 /usr/local/lib/hostingpanel
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
ReadOnlyPaths=/srv/hosting/home
ReadWritePaths=/run/lock
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
  systemctl enable --now hosting-clamav-scan.timer
}
