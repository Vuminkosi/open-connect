#!/usr/bin/env sh
# T2 (+T3 when TIER=t3) smoke for MMS TTS. Bounded waits, sanitized logs.
# Exits 3 when the canonical worker Dockerfile is not yet delivered.
set -eu
SAMPLE_DIR="$(cd "$(dirname "$0")/.." && pwd)"
SHARED="$SAMPLE_DIR/../shared/scripts"
# shellcheck source=../../shared/scripts/lib.sh
. "$SHARED/lib.sh"

DOCKERFILE="$SAMPLE_DIR/../../../workers/mms-tts/Dockerfile"
if [ ! -f "$DOCKERFILE" ]; then
  echo "BLOCKED: mms-tts: canonical Dockerfile missing ($DOCKERFILE) pending worker delivery" >&2
  exit 3
fi

BASE="http://127.0.0.1:${PORT_MMS_TTS:-8087}"
echo "==> T2 mms-tts: waiting for $BASE/health/ready (bounded ${READY_TIMEOUT_SEC}s)"
wait_ready "$BASE/health/ready"
echo "PASS T2 mms-tts: ready"

if [ "${TIER:-t2}" = "t3" ]; then
  echo "==> T3 mms-tts: fixture request to /internal/v1/execute (fake engine, synthetic text)"
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
    echo "FAIL T3 mms-tts: expected HTTP $EXPECTED_STATUS, got $CODE" >&2
    exit 1
  fi
  EXPECTED_STATUS="$CODE" "$PYBIN" "$SHARED/assert_response.py" \
    "$OUT" "$SAMPLE_DIR/fixtures/expected.json"
  "$PYBIN" - "$OUT" <<'PYEOF'
import base64, json, sys
body = json.load(open(sys.argv[1]))
result = body.get("result")
assert isinstance(result, dict), "FAIL T3 mms-tts: result must be an object"
raw = result.get("audioBase64", "")
assert base64.b64decode(raw).startswith(b"FAKE-MMS-WAV"), \
    "FAIL T3 mms-tts: audioBase64 missing FAKE-MMS-WAV marker (fake-labeled harness only)"
print("PASS T3 mms-tts: fake marker FAKE-MMS-WAV present")
PYEOF
  # Negatives: unauthenticated must be 401; unknown kind must be 4xx.
  NOAUTH_CODE="$(curl -sS -o /dev/null -w '%{http_code}' --max-time 30 \
    -H 'Content-Type: application/json' \
    -d @"$BODY" "$BASE/internal/v1/execute")"
  if [ "$NOAUTH_CODE" != "401" ]; then
    echo "FAIL T3 mms-tts: expected HTTP 401 without Authorization, got $NOAUTH_CODE" >&2
    exit 1
  fi
  BADKIND_CODE="$(curl -sS -o /dev/null -w '%{http_code}' --max-time 30 \
    -H 'Content-Type: application/json' \
    -H "Authorization: Bearer $TOKEN" \
    -d '{"schemaVersion":"openconnect.v1","taskId":"t-e2e-bad","kind":"mms.bogus","payload":{}}' \
    "$BASE/internal/v1/execute")"
  case "$BADKIND_CODE" in
    4*) echo "PASS T3 mms-tts: negatives 401/4xx confirmed" ;;
    *) echo "FAIL T3 mms-tts: expected HTTP 4xx for unknown kind, got $BADKIND_CODE" >&2; exit 1 ;;
  esac
  rm -f "$OUT"
  echo "PASS T3 mms-tts: fixture response conforms"
fi
