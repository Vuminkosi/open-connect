#!/usr/bin/env sh
# T2 (+T3 when TIER=t3) smoke for Outbox relay (.NET host). Bounded waits, sanitized logs.
# Exits 3 when the canonical worker Dockerfile is not yet delivered.
set -eu
SAMPLE_DIR="$(cd "$(dirname "$0")/.." && pwd)"
SHARED="$SAMPLE_DIR/../shared/scripts"
# shellcheck source=../../shared/scripts/lib.sh
. "$SHARED/lib.sh"

DOCKERFILE="$SAMPLE_DIR/../../../workers/outbox/Dockerfile"
if [ ! -f "$DOCKERFILE" ]; then
  echo "BLOCKED: outbox: canonical Dockerfile missing ($DOCKERFILE) pending worker delivery" >&2
  exit 3
fi

BASE="http://127.0.0.1:${PORT_OUTBOX:-8090}"
echo "==> T2 outbox: waiting for $BASE/health/ready (bounded ${READY_TIMEOUT_SEC}s)"
wait_ready "$BASE/health/ready"
echo "PASS T2 outbox: ready"

if [ "${TIER:-t2}" = "t3" ]; then
  echo "==> T3 outbox: fixture request to /relay"
  BODY="$SAMPLE_DIR/fixtures/request.json"
  OUT="$SAMPLE_DIR/.t3-response.json"
  CODE="$(curl -sS -o "$OUT" -w '%{http_code}' --max-time 30 \
    -H 'Content-Type: application/json' \
    -d @"$BODY" "$BASE/relay")"
  sanitize_file "$OUT"
  EXPECTED_STATUS="$("$PYBIN" -c "import json,sys;print(json.load(open(sys.argv[1]))['expectedStatus'])" "$SAMPLE_DIR/fixtures/expected.json")"
  if [ "$CODE" != "$EXPECTED_STATUS" ]; then
    echo "FAIL T3 outbox: expected HTTP $EXPECTED_STATUS, got $CODE" >&2
    exit 1
  fi
  EXPECTED_STATUS="$CODE" "$PYBIN" "$SHARED/assert_response.py" \
    "$OUT" "$SAMPLE_DIR/fixtures/expected.json"
  rm -f "$OUT"
  echo "PASS T3 outbox: fixture response conforms"
fi
