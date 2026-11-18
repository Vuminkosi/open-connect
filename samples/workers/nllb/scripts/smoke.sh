#!/usr/bin/env sh
# T2 (+T3 when TIER=t3) smoke for NLLB translation. Bounded waits, sanitized logs.
# Exits 3 when the canonical worker Dockerfile is not yet delivered.
set -eu
SAMPLE_DIR="$(cd "$(dirname "$0")/.." && pwd)"
SHARED="$SAMPLE_DIR/../shared/scripts"
# shellcheck source=../../shared/scripts/lib.sh
. "$SHARED/lib.sh"

DOCKERFILE="$SAMPLE_DIR/../../../workers/nllb/Dockerfile"
if [ ! -f "$DOCKERFILE" ]; then
  echo "BLOCKED: nllb: canonical Dockerfile missing ($DOCKERFILE) pending worker delivery" >&2
  exit 3
fi

BASE="http://127.0.0.1:${PORT_NLLB:-8082}"
echo "==> T2 nllb: waiting for $BASE/health/ready (bounded ${READY_TIMEOUT_SEC}s)"
wait_ready "$BASE/health/ready"
echo "PASS T2 nllb: ready"

if [ "${TIER:-t3}" = "t3" ]; then
  echo "==> T3 nllb: ExecuteRequest fixture to /internal/v1/execute (explicitly fake-labeled engine)"
  TOKEN="${OPENCONNECT_WORKER_TOKEN:-e2e-test-token-do-not-use-in-prod}"
  BODY="$SAMPLE_DIR/fixtures/request.json"
  OUT="$SAMPLE_DIR/.t3-response.json"
  CODE="$(curl -sS -o "$OUT" -w '%{http_code}' --max-time 30 \
    -H 'Content-Type: application/json' \
    -H "Authorization: Bearer $TOKEN" \
    -d @"$BODY" "$BASE/internal/v1/execute")"
  sanitize_file "$OUT"
  EXPECTED_STATUS="$("$PYBIN" -c "import json,sys;print(json.load(open(sys.argv[1]))['expectedStatus'])" "$SAMPLE_DIR/fixtures/expected.json")"
  if [ "$CODE" != "$EXPECTED_STATUS" ]; then
    echo "FAIL T3 nllb: expected HTTP $EXPECTED_STATUS, got $CODE" >&2
    exit 1
  fi
  if ! grep -q 'translatedText' "$OUT"; then
    echo "FAIL T3 nllb: missing result.translatedText in response" >&2
    exit 1
  fi
  if ! grep -q 'fake' "$OUT"; then
    echo "FAIL T3 nllb: missing fake-engine marker in response" >&2
    exit 1
  fi
  EXPECTED_STATUS="$CODE" "$PYBIN" "$SHARED/assert_response.py" \
    "$OUT" "$SAMPLE_DIR/fixtures/expected.json"
  rm -f "$OUT"
  echo "PASS T3 nllb: fixture response conforms (schemaVersion/taskId/result.translatedText + fake marker)"
  echo "==> T3 nllb: negatives (auth enforcement + schema validation)"
  NEG_OUT="$SAMPLE_DIR/.t3-neg-response.json"
  NEG_CODE="$(curl -sS -o "$NEG_OUT" -w '%{http_code}' --max-time 30 \
    -H 'Content-Type: application/json' \
    -d @"$BODY" "$BASE/internal/v1/execute")"
  sanitize_file "$NEG_OUT"; rm -f "$NEG_OUT"
  if [ "$NEG_CODE" != "401" ]; then
    echo "FAIL T3 nllb negative: expected HTTP 401 without token, got $NEG_CODE" >&2
    exit 1
  fi
  NEG_CODE="$(curl -sS -o "$NEG_OUT" -w '%{http_code}' --max-time 30 \
    -H 'Content-Type: application/json' \
    -H "Authorization: Bearer $TOKEN" \
    -d '{"bogus":"body"}' "$BASE/internal/v1/execute")"
  sanitize_file "$NEG_OUT"; rm -f "$NEG_OUT"
  if [ "$NEG_CODE" != "400" ] && [ "$NEG_CODE" != "422" ]; then
    echo "FAIL T3 nllb negative: expected HTTP 400-or-422 for invalid body, got $NEG_CODE" >&2
    exit 1
  fi
  echo "PASS T3 nllb: negatives 401/400-or-422 enforced"
fi
