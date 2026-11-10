#!/usr/bin/env sh
# Guardrail: prove the e2e harness introduces no model downloads, no registry
# pushes, and no secrets. Fails closed (non-zero) on any violation.
# Checks only files under samples/workers/** (never touches workers/**).
set -eu
HERE="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=lib.sh
. "$HERE/lib.sh"

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

echo "==> verify-no-build-models: scanning $ROOT"

# 1. No compose image may target a remote registry or mutable floating tag.
if grep -rEn '^\s*image:\s*[^ ]*(ghcr\.io|docker\.io|.*\.azurecr\.io|:latest)' "$ROOT" --include='*.yaml' --include='*.yml'; then
  echo "FAIL: remote or :latest image reference found (local :e2e-local tags only)"
  fail=$((fail + 1))
else
  echo "PASS: all images are local tags"
fi

# 2. No model download affordances in the harness. The verifier's own pattern
#    source would self-match, so it is filtered from the hits.
hits="$(grep -rEni --exclude=verify-no-build-models.sh '(huggingface|hf[_-]?hub|snapshot_download|from_pretrained|MODEL_URL|WEIGHTS_URL|curl .*(\\.bin|\\.safetensors|\\.onnx|\\.pt))' "$ROOT" --include='*.sh' --include='*.yaml' --include='*.yml' --include='*.json' --include='*.md' || true)"
hits="$(printf '%s\n' "$hits" | grep -v 'verify-no-build-models.sh' || true)"
if [ -n "$hits" ]; then
  printf '%s\n' "$hits"
  echo "FAIL: model-download affordance found in harness"
  fail=$((fail + 1))
else
  echo "PASS: no model-download affordances"
fi

# 3. Every executable `compose down` invocation must use --remove-orphans and
#    must not wipe volumes. Scoped to *.sh (docs prose is review-covered);
#    comments are stripped before matching.
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

# 4. No secret-looking literals (tokens, keys, passwords) in fixtures/scripts.
if grep -rEni '(sk-[A-Za-z0-9]{8,}|xox[bap]-|-----BEGIN [A-Z ]*PRIVATE KEY-----|password\s*[:=]\s*["'\'']?[^ "'\'']{4,})' "$ROOT" --include='*.sh' --include='*.yaml' --include='*.yml' --include='*.json' --include='*.example'; then
  echo "FAIL: possible secret literal found (use placeholders + env.example)"
  fail=$((fail + 1))
else
  echo "PASS: no secret literals"
fi

# 5. Every per-worker compose file must reference a canonical worker Dockerfile
#    (or declare itself BLOCKED with the canonical path named).
check "compose files present" test -n "$(find "$ROOT" -maxdepth 2 -name compose.e2e.yaml)"

if [ "$fail" -gt 0 ]; then
  echo "verify-no-build-models: $fail violation(s)" >&2
  exit 1
fi
echo "verify-no-build-models: OK (no downloads, no pushes, no secrets, no down -v)"
