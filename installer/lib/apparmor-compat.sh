# Temporary compatibility for containerd#14485. Never disable AppArmor or host restrictions.
needs_apparmor_compat() {
  [[ "$1" == ubuntu && "$2" == 26.04 && "$3" == 7.0.0-44-generic && "$4" == v1.35.10+k3s1 ]]
}

install_apparmor_compat() {
  needs_apparmor_compat "$ID" "$VERSION_ID" "$(uname -r)" "$(require k3sVersion)" || return 0
  [[ $(cat /sys/module/apparmor/parameters/enabled 2>/dev/null) == Y ]] || die "AppArmor must be enabled"
  [[ $(cat /proc/sys/kernel/apparmor_restrict_unprivileged_unconfined) == 1 ]] || die "Expected Ubuntu AppArmor hardening"
  [[ $(cat /proc/self/attr/current) == unconfined* ]] || die "Unexpected installer AppArmor profile"
  apt-get install -y apparmor
  local profile="$here/compat/containerd-2.2.10-k3s1.apparmor"
  # Do not silently overwrite an administrator's persistent custom policy.
  [[ ! -e /etc/apparmor.d/cri-containerd.apparmor.d ]] || die "Existing custom containerd AppArmor policy; review manually"
  apparmor_parser -Q -K "$profile"
  apparmor_parser -r -K "$profile"
  grep -Fxq 'cri-containerd.apparmor.d (enforce)' /sys/kernel/security/apparmor/profiles || die "Containerd AppArmor policy not enforcing"
  echo "Applied exact-version AppArmor stacked-peer compatibility (containerd#14485)"
}

# Reapply after boot only while the exact affected version is running.
install_apparmor_compat_boot_hook() {
  needs_apparmor_compat "$ID" "$VERSION_ID" "$(uname -r)" "$(require k3sVersion)" || return 0
  local service=$1
  install -d -m 0755 /usr/local/lib/hostingpanel /etc/systemd/system/"$service".service.d
  install -m 0644 "$here/compat/containerd-2.2.10-k3s1.apparmor" /usr/local/lib/hostingpanel/containerd-2.2.10-k3s1.apparmor
  cat > /usr/local/lib/hostingpanel/apparmor-compat <<'EOF'
#!/bin/bash
set -euo pipefail
source /etc/os-release
[[ "$ID" == ubuntu && "$VERSION_ID" == 26.04 && $(uname -r) == 7.0.0-44-generic ]] || exit 0
[[ $(/usr/local/bin/k3s --version | head -n1 | awk '{print $3}') == v1.35.10+k3s1 ]] || exit 0
[[ $(cat /sys/module/apparmor/parameters/enabled) == Y ]]
[[ $(cat /proc/sys/kernel/apparmor_restrict_unprivileged_unconfined) == 1 ]]
[[ ! -e /etc/apparmor.d/cri-containerd.apparmor.d ]]
apparmor_parser -r -K /usr/local/lib/hostingpanel/containerd-2.2.10-k3s1.apparmor
grep -Fxq 'cri-containerd.apparmor.d (enforce)' /sys/kernel/security/apparmor/profiles
EOF
  chmod 0755 /usr/local/lib/hostingpanel/apparmor-compat
  cat > /etc/systemd/system/"$service".service.d/90-hostingpanel-apparmor-compat.conf <<'EOF'
[Unit]
After=apparmor.service
[Service]
ExecStartPre=/usr/local/lib/hostingpanel/apparmor-compat
EOF
  systemctl daemon-reload
}
