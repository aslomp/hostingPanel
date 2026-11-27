#!/usr/bin/env bash
set -Eeuo pipefail
ns=$(k3s kubectl create namespace "hosting-runtime-check-$(date +%s)-$$" -o jsonpath='{.metadata.name}')
cleanup() {
  local status=$?
  if ((status)); then
    echo "Runtime smoke test failed; namespace $ns retained" >&2
    k3s kubectl -n "$ns" describe pod signal-check || true
    k3s kubectl -n "$ns" logs signal-check || true
  else
    k3s kubectl delete namespace "$ns" --wait=false
  fi
}
trap cleanup EXIT
k3s kubectl -n "$ns" apply -f - <<'EOF'
apiVersion: v1
kind: Pod
metadata:
  name: signal-check
spec:
  restartPolicy: Never
  automountServiceAccountToken: false
  containers:
  - name: check
    image: busybox:1.37.0
    command: ["/bin/sh", "-ec"]
    args:
    - |
      sleep 60 &
      child=$!
      kill -TERM "$child"
      result=0
      wait "$child" || result=$?
      test "$result" = 143
      echo "Container signal check passed"
    securityContext:
      runAsUser: 65534
      runAsGroup: 65534
      allowPrivilegeEscalation: false
      capabilities:
        drop: ["ALL"]
      appArmorProfile:
        type: RuntimeDefault
EOF
k3s kubectl -n "$ns" wait --for=jsonpath='{.status.phase}'=Succeeded pod/signal-check --timeout=120s
k3s kubectl -n "$ns" logs signal-check
