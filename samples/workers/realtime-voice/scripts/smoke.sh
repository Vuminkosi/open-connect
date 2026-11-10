#!/usr/bin/env sh
# T2 (+T3 when TIER=t3) smoke for Realtime voice. Bounded waits, sanitized logs.
# Orchestration-only session flow against POST /internal/v1/execute:
#   voice.session-open -> voice.event(s) with epoch fencing.
# Fake engine is explicitly labeled (capabilities voice.fake-chunks).
set -eu
SAMPLE_DIR="$(cd "$(dirname "$0")/.." && pwd)"
SHARED="$SAMPLE_DIR/../shared/scripts"
# shellcheck source=../../shared/scripts/lib.sh
. "$SHARED/lib.sh"

DOCKERFILE="$SAMPLE_DIR/../../../workers/realtime-voice/Dockerfile"
if [ ! -f "$DOCKERFILE" ]; then
  echo "BLOCKED: realtime-voice: canonical Dockerfile missing ($DOCKERFILE) pending worker delivery" >&2
  exit 3
fi

BASE="http://127.0.0.1:${PORT_REALTIME_VOICE:-8085}"
echo "==> T2 realtime-voice: waiting for $BASE/health/ready (bounded ${READY_TIMEOUT_SEC}s)"
wait_ready "$BASE/health/ready"
echo "PASS T2 realtime-voice: ready"

if [ "${TIER:-t2}" = "t3" ]; then
  TOKEN="${OPENCONNECT_WORKER_TOKEN:-e2e-test-token-do-not-use-in-prod}"
  EXEC="$BASE/internal/v1/execute"
  OUT="$SAMPLE_DIR/.t3-response.json"

  post_execute() {
    # post_execute <body.json> <out.json> : prints HTTP code
    curl -sS -o "$2" -w '%{http_code}' --max-time 30 \
      -H 'Content-Type: application/json' \
      -H "Authorization: Bearer $TOKEN" \
      -d @"$1" "$EXEC"
  }

  echo "==> T3 realtime-voice: assert explicitly fake-labeled engine"
  CAP_OUT="$SAMPLE_DIR/.t3-cap.json"
  CAP_CODE="$(curl -sS -o "$CAP_OUT" -w '%{http_code}' --max-time 30 \
    -H "Authorization: Bearer $TOKEN" "$BASE/internal/v1/capabilities")"
  sanitize_file "$CAP_OUT"
  if [ "$CAP_CODE" != "200" ]; then
    echo "FAIL T3 realtime-voice: capabilities expected HTTP 200, got $CAP_CODE" >&2
    exit 1
  fi
  if ! grep -q 'fake-engine' "$CAP_OUT" || ! grep -q 'voice.fake-chunks' "$CAP_OUT"; then
    echo "FAIL T3 realtime-voice: missing fake-engine label in capabilities" >&2
    exit 1
  fi
  rm -f "$CAP_OUT"
  echo "PASS T3 realtime-voice: engine fake-engine v0.1.0 explicitly labeled"

  echo "==> T3 realtime-voice: session-open -> voice.event session flow"
  EXPECTED_STATUS="$("$PYBIN" -c "import json,sys;print(json.load(open(sys.argv[1]))['expectedStatus'])" "$SAMPLE_DIR/fixtures/expected.json")"

  CODE="$(post_execute "$SAMPLE_DIR/fixtures/request.json" "$OUT")"
  sanitize_file "$OUT"
  if [ "$CODE" != "$EXPECTED_STATUS" ]; then
    echo "FAIL T3 realtime-voice session-open: expected HTTP $EXPECTED_STATUS, got $CODE" >&2
    exit 1
  fi
  EXPECTED_STATUS="$CODE" "$PYBIN" "$SHARED/assert_response.py" \
    "$OUT" "$SAMPLE_DIR/fixtures/expected.json"
  if ! grep -q '"state"' "$OUT"; then
    echo "FAIL T3 realtime-voice session-open: missing state in response" >&2
    exit 1
  fi

  for STEP in "event-begin-listening.json:listening" "event-transcript-final.json:transcribing" "event-begin-reasoning.json:reasoning" "event-begin-speaking.json:speaking"; do
    FIX="${STEP%%:*}"
    WANT_STATE="${STEP##*:}"
    CODE="$(post_execute "$SAMPLE_DIR/fixtures/$FIX" "$OUT")"
    sanitize_file "$OUT"
    if [ "$CODE" != "$EXPECTED_STATUS" ]; then
      echo "FAIL T3 realtime-voice $FIX: expected HTTP $EXPECTED_STATUS, got $CODE" >&2
      exit 1
    fi
    EXPECTED_STATUS="$CODE" "$PYBIN" "$SHARED/assert_response.py" \
      "$OUT" "$SAMPLE_DIR/fixtures/expected.json"
    if ! grep -q "\"$WANT_STATE\"" "$OUT"; then
      echo "FAIL T3 realtime-voice $FIX: expected state $WANT_STATE in response" >&2
      exit 1
    fi
  done
  if ! grep -q '"chunks"' "$OUT"; then
    echo "FAIL T3 realtime-voice begin-speaking: missing fake chunks in result" >&2
    exit 1
  fi
  rm -f "$OUT"
  echo "PASS T3 realtime-voice: session flow idle->listening->transcribing->reasoning->speaking with fake chunks"

  echo "==> T3 realtime-voice: negatives (401/4xx + stale-epoch 409)"
  NEG_OUT="$SAMPLE_DIR/.t3-neg-response.json"
  NEG_CODE="$(curl -sS -o "$NEG_OUT" -w '%{http_code}' --max-time 30 \
    -H 'Content-Type: application/json' \
    -d @"$SAMPLE_DIR/fixtures/request.json" "$EXEC")"
  sanitize_file "$NEG_OUT"; rm -f "$NEG_OUT"
  if [ "$NEG_CODE" != "401" ]; then
    echo "FAIL T3 realtime-voice negative: expected HTTP 401 without token, got $NEG_CODE" >&2
    exit 1
  fi
  NEG_CODE="$(curl -sS -o "$NEG_OUT" -w '%{http_code}' --max-time 30 \
    -H 'Content-Type: application/json' \
    -H "Authorization: Bearer $TOKEN" \
    -d '{"bogus":"body"}' "$EXEC")"
  sanitize_file "$NEG_OUT"; rm -f "$NEG_OUT"
  if [ "$NEG_CODE" != "400" ] && [ "$NEG_CODE" != "422" ]; then
    echo "FAIL T3 realtime-voice negative: expected HTTP 400-or-422 for invalid body, got $NEG_CODE" >&2
    exit 1
  fi
  NEG_CODE="$(post_execute "$SAMPLE_DIR/fixtures/event-stale-epoch.json" "$NEG_OUT")"
  sanitize_file "$NEG_OUT"; rm -f "$NEG_OUT"
  if [ "$NEG_CODE" != "409" ]; then
    echo "FAIL T3 realtime-voice negative: expected HTTP 409 for stale epoch, got $NEG_CODE" >&2
    exit 1
  fi
  echo "PASS T3 realtime-voice: negatives 401/400-or-422/409 enforced"
fi
