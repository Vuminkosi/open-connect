#!/usr/bin/env sh
# T2 (+T3 when TIER=t3) smoke for Kokoro TTS. Bounded waits, sanitized logs.
# Exits 3 when the canonical worker Dockerfile is not yet delivered.
set -eu
SAMPLE_DIR="$(cd "$(dirname "$0")/.." && pwd)"
SHARED="$SAMPLE_DIR/../shared/scripts"
# shellcheck source=../../shared/scripts/lib.sh
. "$SHARED/lib.sh"

DOCKERFILE="$SAMPLE_DIR/../../../workers/kokoro/Dockerfile"
if [ ! -f "$DOCKERFILE" ]; then
  echo "BLOCKED: kokoro: canonical Dockerfile missing ($DOCKERFILE) pending worker delivery" >&2
  exit 3
fi

BASE="http://127.0.0.1:${PORT_KOKORO:-8086}"
echo "==> T2 kokoro: waiting for $BASE/health/ready (bounded ${READY_TIMEOUT_SEC}s)"
wait_ready "$BASE/health/ready"
echo "PASS T2 kokoro: ready"

if [ "${TIER:-t2}" = "t3" ]; then
  echo "==> T3 kokoro: execute envelope to /internal/v1/execute (fake engine, synthetic text)"
  TOKEN="${OPENCONNECT_WORKER_TOKEN:-e2e-test-token-do-not-use-in-prod}"
  BODY="$SAMPLE_DIR/fixtures/request.json"
  OUT="$(mktemp "${TMPDIR:-/tmp}/kokoro-t3-resp.XXXXXX.json")"
  NEG="$(mktemp "${TMPDIR:-/tmp}/kokoro-t3-neg.XXXXXX.json")"
  trap 'rm -f "$OUT" "$NEG"' EXIT INT TERM
  CODE="$(curl -sS -o "$OUT" -w '%{http_code}' --max-time 30 \
    -H 'Content-Type: application/json' \
    -H "Authorization: Bearer $TOKEN" \
    -d @"$BODY" "$BASE/internal/v1/execute")"
  sanitize_file "$OUT"
  EXPECTED_STATUS="$("$PYBIN" -c "import json,sys;print(json.load(open(sys.argv[1]))['expectedStatus'])" "$SAMPLE_DIR/fixtures/expected.json")"
  if [ "$CODE" != "$EXPECTED_STATUS" ]; then
    echo "FAIL T3 kokoro: expected HTTP $EXPECTED_STATUS, got $CODE" >&2
    exit 1
  fi
  EXPECTED_STATUS="$CODE" "$PYBIN" "$SHARED/assert_response.py" \
    "$OUT" "$SAMPLE_DIR/fixtures/expected.json"
  "$PYBIN" - "$OUT" <<'PYEOF'
import base64, json, sys
body = json.load(open(sys.argv[1]))
result = body.get("result")
assert isinstance(result, dict), "result must be an object"
b64 = result.get("audioBase64")
assert isinstance(b64, str) and b64, "result.audioBase64 must be present"
raw = base64.b64decode(b64)
assert raw.startswith(b"FAKE-"), "fake-engine marker (FAKE- prefix) missing"
print("PASS T3 kokoro: audioBase64 present with fake-engine marker")
PYEOF
  # Negatives: no token -> 401; wrong token -> 401.
  for AUTH in "none" "wrong"; do
    if [ "$AUTH" = "none" ]; then
      NCODE="$(curl -sS -o "$NEG" -w '%{http_code}' --max-time 30 \
        -H 'Content-Type: application/json' \
        -d @"$BODY" "$BASE/internal/v1/execute")"
    else
      NCODE="$(curl -sS -o "$NEG" -w '%{http_code}' --max-time 30 \
        -H 'Content-Type: application/json' \
        -H 'Authorization: Bearer wrong-test-token' \
        -d @"$BODY" "$BASE/internal/v1/execute")"
    fi
    sanitize_file "$NEG"
    if [ "$NCODE" != "401" ]; then
      echo "FAIL T3 kokoro negative ($AUTH token): expected HTTP 401, got $NCODE" >&2
      exit 1
    fi
  done
  # Negative: unknown kind -> 422.
  "$PYBIN" -c "import json;doc=json.load(open('$BODY'));doc['kind']='no.such.kind';doc['taskId']='e2e-kokoro-t3-neg-kind';json.dump(doc,open('$NEG','w'))"
  NCODE="$(curl -sS -o "$NEG.out" -w '%{http_code}' --max-time 30 \
    -H 'Content-Type: application/json' \
    -H "Authorization: Bearer $TOKEN" \
    -d @"$NEG" "$BASE/internal/v1/execute")"
  rm -f "$NEG.out"
  sanitize_file "$NEG"
  case "$NCODE" in
    4*) echo "PASS T3 kokoro negatives: 401/4xx enforced" ;;
    *) echo "FAIL T3 kokoro negative (unknown kind): expected 4xx, got $NCODE" >&2; exit 1 ;;
  esac
  rm -f "$OUT" "$NEG"
  trap - EXIT INT TERM
  echo "PASS T3 kokoro: fixture response conforms"
fi
