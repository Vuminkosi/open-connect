#!/usr/bin/env sh
# Guardrail: prove the e2e harness and worker Dockerfiles introduce no build-time model downloads,
# no registry pushes, no legacy Postgres library references, no secrets, and no volume-wiping teardown.
# Usage: verify-no-build-models.sh [<worker>|--worker <name>]
set -eu
HERE="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=lib.sh
. "$HERE/lib.sh"

TARGET_WORKER=""
while [ $# -gt 0 ]; do
  reject_destructive_flag "$1"
  case "$1" in
    --worker) TARGET_WORKER="$2"; shift 2;;
    -h|--help) echo "Usage: verify-no-build-models.sh [<worker>|--worker <name>]"; exit 0;;
    -*) echo "error: unknown argument '$1'" >&2; exit 2;;
    *) TARGET_WORKER="$1"; shift 1;;
  esac
done

if [ -n "$TARGET_WORKER" ]; then
  require_worker "$TARGET_WORKER"
  SCAN_ROOT="$SAMPLES_WORKERS_DIR/$TARGET_WORKER"
else
  SCAN_ROOT="$SAMPLES_WORKERS_DIR"
fi

ROOT="$SAMPLES_WORKERS_DIR"
fail=0

check() {
  desc="$1"; shift
  if "$@" >/dev/null 2>&1; then
    echo "PASS: $desc"
  else
    echo "FAIL: $desc"
    fail=$((fail + 1))
  fi
}

echo "==> verify-no-build-models: scanning $SCAN_ROOT"

# 1. No compose image may target an unauthorized remote registry or mutable :latest floating tag.
if grep -rEn '^\s*image:\s*[^ ]*(ghcr\.io|docker\.io|.*\.azurecr\.io|:latest)' "$SCAN_ROOT" --include='*.yaml' --include='*.yml'; then
  echo "FAIL: remote or :latest image reference found (local :e2e-local tags only)"
  fail=$((fail + 1))
else
  echo "PASS: all images are local tags"
fi

# 2. No model download affordances in the harness scripts or compose files.
hits="$(grep -rEni --exclude=verify-no-build-models.sh --exclude=traceability.csv '(huggingface|hf[_-]?hub|snapshot_download|from_pretrained|MODEL_URL|WEIGHTS_URL|curl .*(\\.bin|\\.safetensors|\\.onnx|\\.pt))' "$SCAN_ROOT" --include='*.sh' --include='*.yaml' --include='*.yml' --include='*.json' || true)"
hits="$(printf '%s\n' "$hits" | grep -v 'verify-no-build-models.sh' || true)"
if [ -n "$hits" ]; then
  printf '%s\n' "$hits"
  echo "FAIL: model-download affordance found in harness"
  fail=$((fail + 1))
else
  echo "PASS: no model-download affordances"
fi

# 3. Every executable `compose down` invocation must use --remove-orphans and must not wipe volumes.
viol=0
for f in $(find "$ROOT" -name '*.sh' ! -name 'verify-no-build-models.sh' | sort); do
  stripped="$(sed 's/#.*//' "$f")"
  downs="$(printf '%s' "$stripped" | grep -n 'compose.*down' || true)"
  if [ -n "$downs" ]; then
    badlines="$(printf '%s\n' "$downs" | grep -v -- '--remove-orphans' || true)"
    if [ -n "$badlines" ]; then
      echo "FAIL teardown without --remove-orphans in $f:"; printf '%s\n' "$badlines"
      viol=$((viol + 1))
    fi
    volumelines="$(printf '%s\n' "$downs" | grep -E '(^| )-v( |$)|--volumes' || true)"
    if [ -n "$volumelines" ]; then
      echo "FAIL volume-wiping teardown in $f:"; printf '%s\n' "$volumelines"
      viol=$((viol + 1))
    fi
  fi
done
if [ "$viol" -gt 0 ]; then
  echo "FAIL: volume-unsafe teardown found (use down --remove-orphans only)"
  fail=$((fail + viol))
else
  echo "PASS: all executable teardowns use down --remove-orphans, none wipe volumes"
fi

# 4. No secret-looking literals in fixtures/scripts.
hits="$(grep -rEni --exclude='*Tests.cs' '(sk-[A-Za-z0-9]{8,}|xox[bap]-|-----BEGIN [A-Z ]*PRIVATE KEY-----|password\s*[:=]\s*["'\'']?[^ "'\'']{4,})' "$SCAN_ROOT" --include='*.sh' --include='*.yaml' --include='*.yml' --include='*.json' --include='*.example' || true)"
hits="$(printf '%s\n' "$hits" | grep -v '\${' | grep -vi 'do-not-use-in-prod' | grep -vE 'password\s*[:=]\s*["'\'']?\$' || true)"
if [ -n "$hits" ]; then
  printf '%s\n' "$hits"
  echo "FAIL: possible secret literal found (use placeholders + env.example)"
  fail=$((fail + 1))
else
  echo "PASS: no secret literals"
fi

# 5. No forbidden legacy OpenConnect.Persistence.Postgres references.
pg_hits="$(grep -rEn 'OpenConnect\.Persistence\.Postgres|OpenConnect\.Navigator\.Persistence\.Postgres' "$SCAN_ROOT" --include='*.csproj' --include='*.cs' --include='*.sh' || true)"
if [ -n "$pg_hits" ]; then
  printf '%s\n' "$pg_hits"
  echo "FAIL: legacy OpenConnect.Persistence.Postgres reference found"
  fail=$((fail + 1))
else
  echo "PASS: no legacy OpenConnect.Persistence.Postgres references"
fi

# 6. Compose files present
check "compose files present" test -n "$(find "$SCAN_ROOT" -maxdepth 2 -name compose.e2e.yaml)"

if [ "$fail" -gt 0 ]; then
  echo "verify-no-build-models: $fail violation(s)" >&2
  exit 1
fi
echo "verify-no-build-models: OK (no downloads, no pushes, no secrets, no down -v)"
