# Conservative clean-host budgets; these are not Kubernetes sizing guarantees.
check_host_capacity() {
  local role=$1 min_disk min_ram free_kib total_kib
  case "$role" in
    --full-server) min_disk=25; min_ram=8 ;;
    --storage-server) min_disk=5; min_ram=4 ;;
    --worker) min_disk=10; min_ram=2 ;;
    *) die "Unknown preflight role" ;;
  esac
  free_kib=$(df -Pk / | awk 'NR==2 {print $4}')
  total_kib=$(awk '/^MemTotal:/ {print $2}' /proc/meminfo)
  [[ "$free_kib" =~ ^[0-9]+$ && "$total_kib" =~ ^[0-9]+$ ]] || die "Cannot determine host capacity"
  printf 'Preflight: root free %s MiB; RAM %s MiB; role %s\n' "$((free_kib/1024))" "$((total_kib/1024))" "$role"
  if ((free_kib < min_disk * 1024 * 1024)); then
    df -h /
    if command -v vgs >/dev/null; then
      vgs -o vg_name,vg_size,vg_free || true
      echo "If the root logical volume has free VG extents, extend it after checking the layout." >&2
    fi
    die "At least ${min_disk} GiB free on / required for this installation role. No filesystem changes made."
  fi
  # MemTotal excludes firmware/kernel reservations; allow a small difference from nominal VM RAM.
  if ((total_kib < min_ram * 1024 * 1024 * 9 / 10)); then
    die "At least ${min_ram} GiB nominal RAM recommended and required by this installer role."
  fi
}
