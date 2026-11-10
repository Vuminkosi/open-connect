#!/usr/bin/env sh
# T2 (+T3 when TIER=t3) smoke for Navigator Playwright. Bounded waits, sanitized logs.
# Exits 3 when the canonical worker Dockerfile is not yet delivered.
# Facade contract (workers/navigator-playwright/src/server.js):
#   POST /internal/v1/execute (Bearer NAVIGATOR_WORKER_TOKEN) with {tool,args}
#   -> 200 {ok,tool,...}. No /jobs route exists (option A: sample follows worker).
set -eu
SAMPLE_DIR="$(cd "$(dirname "$0")/.." && pwd)"
SHARED="$SAMPLE_DIR/../shared/scripts"
# shellcheck source=../../shared/scripts/lib.sh
. "$SHARED/lib.sh"

DOCKERFILE="$SAMPLE_DIR/../../../workers/navigator-playwright/Dockerfile"
if [ ! -f "$DOCKERFILE" ]; then
  echo "BLOCKED: navigator-playwright: canonical Dockerfile missing ($DOCKERFILE) pending worker delivery" >&2
  exit 3
fi

BASE="http://127.0.0.1:${PORT_NAVIGATOR_PLAYWRIGHT:-8088}"
echo "==> T2 navigator-playwright: waiting for $BASE/health/ready (bounded ${READY_TIMEOUT_SEC}s)"
wait_ready "$BASE/health/ready"
echo "PASS T2 navigator-playwright: ready"

if [ "${TIER:-t2}" = "t3" ]; then
  echo "==> T3 navigator-playwright: fixture execute-tool request to /internal/v1/execute"
  TOKEN="${NAVIGATOR_WORKER_TOKEN:-e2e-test-token-do-not-use-in-prod}"
  BODY="$SAMPLE_DIR/fixtures/request.json"
  OUT="$SAMPLE_DIR/.t3-response.json"
  CODE="$(curl -sS -o "$OUT" -w '%{http_code}' --max-time 30 \
    -H 'Content-Type: application/json' \
    -H "Authorization: Bearer $TOKEN" \
    -d @"$BODY" "$BASE/internal/v1/execute")"
  sanitize_file "$OUT"
  EXPECTED_STATUS="$("$PYBIN" -c "import json,sys;print(json.load(open(sys.argv[1]))['expectedStatus'])" "$SAMPLE_DIR/fixtures/expected.json")"
  if [ "$CODE" != "$EXPECTED_STATUS" ]; then
    echo "FAIL T3 navigator-playwright: expected HTTP $EXPECTED_STATUS, got $CODE" >&2
    exit 1
  fi
  EXPECTED_STATUS="$CODE" "$PYBIN" "$SHARED/assert_response.py" \
    "$OUT" "$SAMPLE_DIR/fixtures/expected.json"
  rm -f "$OUT"

  echo "==> T3 navigator-playwright: negative probes (no/wrong token, unapproved origin)"
  NEG_OUT="$SAMPLE_DIR/.t3-negative.json"
  # No token -> 401 (or 503 if worker token unset -> fail closed).
  NEG_CODE="$(curl -sS -o "$NEG_OUT" -w '%{http_code}' --max-time 30 \
    -H 'Content-Type: application/json' \
    -d @"$BODY" "$BASE/internal/v1/execute")"
  case "$NEG_CODE" in
    401|503) echo "PASS T3 navigator-playwright: no-token denied ($NEG_CODE)" ;;
    *) echo "FAIL T3 navigator-playwright: no-token probe expected 401/503, got $NEG_CODE" >&2; rm -f "$NEG_OUT"; exit 1 ;;
  esac
  # Wrong token -> 401.
  NEG_CODE="$(curl -sS -o "$NEG_OUT" -w '%{http_code}' --max-time 30 \
    -H 'Content-Type: application/json' \
    -H 'Authorization: Bearer wrong-test-token' \
    -d @"$BODY" "$BASE/internal/v1/execute")"
  case "$NEG_CODE" in
    401) echo "PASS T3 navigator-playwright: wrong-token denied (401)" ;;
    *) echo "FAIL T3 navigator-playwright: wrong-token probe expected 401, got $NEG_CODE" >&2; rm -f "$NEG_OUT"; exit 1 ;;
  esac
  # Unapproved origin (exact-origin allowlist; fixture allowlist is https://example.com) -> 403.
  NEG_CODE="$(curl -sS -o "$NEG_OUT" -w '%{http_code}' --max-time 30 \
    -H 'Content-Type: application/json' \
    -H "Authorization: Bearer $TOKEN" \
    -d '{"tool":"navigate","args":{"url":"https://unapproved.example.org/"}}' \
    "$BASE/internal/v1/execute")"
  case "$NEG_CODE" in
    403) echo "PASS T3 navigator-playwright: unapproved-origin denied (403)" ;;
    *) echo "FAIL T3 navigator-playwright: unapproved-origin probe expected 403, got $NEG_CODE" >&2; rm -f "$NEG_OUT"; exit 1 ;;
  esac
  rm -f "$NEG_OUT"
  echo "PASS T3 navigator-playwright: fixture response conforms"
fi
