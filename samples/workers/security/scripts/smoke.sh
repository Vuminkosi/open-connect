#!/usr/bin/env sh
# T2 (+T3 when TIER=t3/integration) smoke for Security. Bounded waits, sanitized logs.
# Covers WSE2E-AT-001..008 runtime assertions against the real security worker container.
set -eu
SAMPLE_DIR="$(cd "$(dirname "$0")/.." && pwd)"
SHARED="$SAMPLE_DIR/../shared/scripts"
# shellcheck source=../../shared/scripts/lib.sh
. "$SHARED/lib.sh"

DOCKERFILE="$SAMPLE_DIR/../../../workers/security/Dockerfile"
if [ ! -f "$DOCKERFILE" ]; then
  echo "BLOCKED: security: canonical Dockerfile missing ($DOCKERFILE) pending worker delivery" >&2
  exit 3
fi

BASE="http://127.0.0.1:${PORT_SECURITY:-8080}"
echo "==> T2 security: waiting for $BASE/health/ready (bounded ${READY_TIMEOUT_SEC}s)"
wait_ready "$BASE/health/ready"
echo "PASS T2 security: ready"

if [ "${TIER:-t2}" = "t3" ] || [ "${TIER:-t2}" = "integration" ]; then
  TOKEN="${OPENCONNECT_SCAN_TOKEN:-e2e-test-token-do-not-use-in-prod}"
  echo "==> T3 security: fixture request to /scan (WSE2E-AT-001, WSE2E-AT-003)"
  BODY="$SAMPLE_DIR/fixtures/request.json"
  OUT="$SAMPLE_DIR/.t3-response.json"
  NEG="$SAMPLE_DIR/.t3-neg.json"
  trap 'rm -f "$OUT" "$NEG"' EXIT INT TERM

  CODE="$(curl -sS -o "$OUT" -w '%{http_code}' --max-time 30 \
    -H 'Content-Type: application/json' \
    -H "Authorization: Bearer $TOKEN" \
    -d @"$BODY" "$BASE/scan")"
  sanitize_file "$OUT"
  EXPECTED_STATUS="$("$PYBIN" -c "import json,sys;print(json.load(open(sys.argv[1]))['expectedStatus'])" "$SAMPLE_DIR/fixtures/expected.json")"
  if [ "$CODE" != "$EXPECTED_STATUS" ]; then
    echo "FAIL T3 security: expected HTTP $EXPECTED_STATUS, got $CODE" >&2
    exit 1
  fi
  EXPECTED_STATUS="$CODE" "$PYBIN" "$SHARED/assert_response.py" \
    "$OUT" "$SAMPLE_DIR/fixtures/expected.json"

  echo "==> T3 security: rules-pii golden spans + unicode offsets (WSE2E-AT-003)"
  PII_REQ="$("$PYBIN" -c "import json,sys;print(json.dumps(json.load(open(sys.argv[1]))['request']))" "$SAMPLE_DIR/fixtures/rules-pii.json")"
  CODE="$(printf '%s' "$PII_REQ" | curl -sS -o "$OUT" -w '%{http_code}' --max-time 30 \
    -H 'Content-Type: application/json' \
    -H "Authorization: Bearer $TOKEN" \
    -d @- "$BASE/scan")"
  sanitize_file "$OUT"
  if [ "$CODE" != "200" ]; then
    echo "FAIL T3 security rules-pii: expected HTTP 200, got $CODE" >&2
    exit 1
  fi

  echo "==> T3 security: negatives (401 unauthenticated, 503 missing detector fail-closed WSE2E-AT-002/004)"
  NCODE="$(curl -sS -o "$NEG" -w '%{http_code}' --max-time 30 \
    -H 'Content-Type: application/json' \
    -d @"$BODY" "$BASE/scan")"
  sanitize_file "$NEG"
  if [ "$NCODE" != "401" ]; then
    echo "FAIL T3 security negative (no auth): expected HTTP 401, got $NCODE" >&2
    exit 1
  fi

  INJ_REQ="$("$PYBIN" -c "import json,sys;print(json.dumps(json.load(open(sys.argv[1]))['cases'][0]['request']))" "$SAMPLE_DIR/fixtures/prompt-injection.json")"
  NCODE="$(printf '%s' "$INJ_REQ" | curl -sS -o "$NEG" -w '%{http_code}' --max-time 30 \
    -H 'Content-Type: application/json' \
    -H "Authorization: Bearer $TOKEN" \
    -d @- "$BASE/scan")"
  sanitize_file "$NEG"
  if [ "$NCODE" != "503" ]; then
    echo "FAIL T3 security negative (missing deberta-injection detector): expected HTTP 503 fail-closed, got $NCODE" >&2
    exit 1
  fi

  rm -f "$OUT" "$NEG"
  trap - EXIT INT TERM
  echo "PASS T3 security: fixture response and fail-closed negatives conform"
fi
