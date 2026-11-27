#!/usr/bin/env bash
set -Eeuo pipefail
# Use a unique namespace so no existing claim or workload is touched.
ns=$(k3s kubectl create namespace "hosting-storage-check-$(date +%s)-$$" -o jsonpath='{.metadata.name}')
cleanup() {
  local status=$?
  if ((status)); then
    echo "Storage test failed; namespace $ns retained for diagnostics" >&2
    k3s kubectl -n "$ns" get pods,pvc -o wide || true
    k3s kubectl -n "$ns" get events --sort-by=.lastTimestamp || true
  else
    # Retain-policy backing volumes are deliberately not destroyed by this test.
    k3s kubectl delete namespace "$ns" --wait=false
  fi
}
trap cleanup EXIT
for protocol in nfs iscsi; do
  access=ReadWriteOnce
  [[ "$protocol" != nfs ]] || access=ReadWriteMany
  k3s kubectl -n "$ns" apply -f - <<EOF
apiVersion: v1
kind: PersistentVolumeClaim
metadata:
  name: $protocol
spec:
  storageClassName: zfs-$protocol
  accessModes: [$access]
  resources:
    requests:
      storage: 1Gi
EOF
  for phase in write read; do
    command='echo hosting-storage-ok > /data/check; sync'
    [[ "$phase" != read ]] || command='test "$(cat /data/check)" = hosting-storage-ok'
    # Do not let a previous completed pod conceal a failed remount.
    k3s kubectl -n "$ns" apply -f - <<EOF
apiVersion: v1
kind: Pod
metadata:
  name: $protocol-$phase
spec:
  restartPolicy: Never
  automountServiceAccountToken: false
  containers:
  - name: check
    image: busybox:1.37.0
    command: ["/bin/sh", "-ec"]
    args:
    - |
      $command
    securityContext:
      allowPrivilegeEscalation: false
      capabilities:
        drop: ["ALL"]
    volumeMounts:
    - name: data
      mountPath: /data
  volumes:
  - name: data
    persistentVolumeClaim:
      claimName: $protocol
EOF
    k3s kubectl -n "$ns" wait --for=jsonpath='{.status.phase}'=Succeeded "pod/$protocol-$phase" --timeout=300s
    k3s kubectl -n "$ns" delete "pod/$protocol-$phase" --wait=true --timeout=120s
  done
done
