#!/usr/bin/env bash
set -euo pipefail

# Download the exact Swagger contract of the DirectAdmin installation.
# Example: DA_ORIGIN=https://da.example:2222 ./scripts/sync-directadmin-swagger.sh
: "${DA_ORIGIN:?Set DA_ORIGIN to the HTTPS origin of your DirectAdmin instance}"
if [[ "$DA_ORIGIN" != https://* ]]; then
  echo "DA_ORIGIN must use HTTPS" >&2
  exit 2
fi

out="${1:-spec/directadmin.swagger.json}"
mkdir -p "$(dirname "$out")"
tmp="$(mktemp)"
trap 'rm -f "$tmp"' EXIT

curl --fail --show-error --silent --location --proto '=https' --proto-redir '=https' \
  --max-time 30 --output "$tmp" "${DA_ORIGIN%/}/static/swagger.json"

python3 - "$tmp" <<'PY'
import json, sys
with open(sys.argv[1], encoding="utf-8") as f:
    doc = json.load(f)
assert doc.get("swagger") == "2.0", "Expected DirectAdmin Swagger 2.0"
assert isinstance(doc.get("paths"), dict), "Missing paths object"
print(f"Validated {len(doc['paths'])} DirectAdmin Swagger paths")
PY
mv "$tmp" "$out"
echo "Saved: $out"
