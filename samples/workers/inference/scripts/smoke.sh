#!/usr/bin/env sh
# T2 (+T3 when TIER=t3) smoke for Inference. Bounded waits, sanitized logs.
# Exits 3 when the canonical worker Dockerfile is not yet delivered.
set -eu
SAMPLE_DIR="$(cd "$(dirname "$0")/.." && pwd)"
SHARED="$SAMPLE_DIR/../shared/scripts"
# shellcheck source=../../shared/scripts/lib.sh
. "$SHARED/lib.sh"

DOCKERFILE="$SAMPLE_DIR/../../../workers/inference/Dockerfile"
if [ ! -f "$DOCKERFILE" ]; then
  echo "BLOCKED: inference: canonical Dockerfile missing ($DOCKERFILE) pending worker delivery" >&2
  exit 3
fi

BASE="http://127.0.0.1:${PORT_INFERENCE:-8083}"
echo "==> T2 inference: waiting for $BASE/health/ready (bounded ${READY_TIMEOUT_SEC}s)"
wait_ready "$BASE/health/ready"
echo "PASS T2 inference: ready"

if [ "${TIER:-t2}" = "t3" ]; then
  echo "==> T3 inference: ExecuteRequest fixture to /internal/v1/execute (explicitly fake-labeled engine)"
  TOKEN="${OPENCONNECT_SCAN_TOKEN:-e2e-test-token-do-not-use-in-prod}"
  BODY_TMP="$SAMPLE_DIR/.t3-execute-request.json"
  printf '%s' '{"schemaVersion":"openconnect.v1","requestId":"123e4567-e89b-42d3-a456-426614174003","input":{"messages":[{"role":"user","content":"Say hello in one word."}],"maxTokens":8}}' > "$BODY_TMP"
  OUT="$SAMPLE_DIR/.t3-response.json"
  CODE="$(curl -sS -o "$OUT" -w '%{http_code}' --max-time 30 \
    -H 'Content-Type: application/json' \
    -H "Authorization: Bearer $TOKEN" \
    -d @"$BODY_TMP" "$BASE/internal/v1/execute")"
  sanitize_file "$OUT"
  rm -f "$BODY_TMP"
  if [ "$CODE" != "200" ]; then
    echo "FAIL T3 inference: expected HTTP 200, got $CODE" >&2
    exit 1
  fi
  if ! grep -q 'fake-inference response to:' "$OUT"; then
    echo "FAIL T3 inference: missing fake-echo marker in response" >&2
    exit 1
  fi
  # fixtures/expected.json targets the legacy /complete shape, so T3 asserts the
  # canonical ExecuteResponse contract inline: schemaVersion/requestId/output
  # fields plus the explicitly fake-labeled echo marker.
  "$PYBIN" -c "import json,sys; b=json.load(open(sys.argv[1])); assert all(k in b for k in ('schemaVersion','requestId','output')), 'missing ExecuteResponse fields'; assert 'fake-inference response to:' in b['output'], 'missing fake-echo marker'; print('PASS T3 inference: ExecuteResponse has schemaVersion/requestId/output + fake-echo marker')" "$OUT"
  rm -f "$OUT"
  echo "PASS T3 inference: fixture response conforms"
  echo "==> T3 inference: negatives (auth enforcement + schema validation)"
  NEG_OUT="$SAMPLE_DIR/.t3-neg-response.json"
  NEG_CODE="$(curl -sS -o "$NEG_OUT" -w '%{http_code}' --max-time 30 \
    -H 'Content-Type: application/json' \
    -d '{"schemaVersion":"openconnect.v1","requestId":"123e4567-e89b-42d3-a456-426614174003","input":{"messages":[{"role":"user","content":"hi"}],"maxTokens":8}}' \
    "$BASE/internal/v1/execute")"
  sanitize_file "$NEG_OUT"; rm -f "$NEG_OUT"
  if [ "$NEG_CODE" != "401" ]; then
    echo "FAIL T3 inference negative: expected HTTP 401 without token, got $NEG_CODE" >&2
    exit 1
  fi
  NEG_CODE="$(curl -sS -o "$NEG_OUT" -w '%{http_code}' --max-time 30 \
    -H 'Content-Type: application/json' \
    -H "Authorization: Bearer $TOKEN" \
    -d '{"bogus":"body"}' "$BASE/internal/v1/execute")"
  sanitize_file "$NEG_OUT"; rm -f "$NEG_OUT"
  if [ "$NEG_CODE" != "422" ]; then
    echo "FAIL T3 inference negative: expected HTTP 422 for invalid body, got $NEG_CODE" >&2
    exit 1
  fi
  echo "PASS T3 inference: negatives 401/422 enforced"
fi
