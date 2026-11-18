#!/usr/bin/env sh
# T2 (+T3 when TIER=t3) VAD client/server smoke. No extra image: the server is
# the transcription worker image; this script is the host-side client.
# Exits 3 when the canonical transcription Dockerfile is not yet delivered.
set -eu
SAMPLE_DIR="$(cd "$(dirname "$0")/.." && pwd)"
SHARED="$SAMPLE_DIR/../shared/scripts"
# shellcheck source=../../shared/scripts/lib.sh
. "$SHARED/lib.sh"

DOCKERFILE="$SAMPLE_DIR/../../../workers/transcription/Dockerfile"
if [ ! -f "$DOCKERFILE" ]; then
  echo "BLOCKED voice-vad-client-server: canonical Dockerfile missing ($DOCKERFILE)" >&2
  exit 3
fi

BASE="http://127.0.0.1:${PORT_TRANSCRIPTION:-8084}"
echo "==> T2 voice-vad-client-server: waiting for $BASE/health/ready (bounded ${READY_TIMEOUT_SEC}s)"
wait_ready "$BASE/health/ready"
echo "PASS T2 voice-vad-client-server: ready"

if [ "${TIER:-t2}" = "t3" ]; then
  echo "==> T3 voice-vad-client-server: VAD chunk sequence"
  OUT="$SAMPLE_DIR/.t3-response.json"
  for chunk in chunk-0 chunk-1; do
    curl -sS -o /dev/null --max-time 30 -H 'Content-Type: application/json' \
      -d @"$SAMPLE_DIR/fixtures/$chunk.json" "$BASE/transcribe"
  done
  CODE="$(curl -sS -o "$OUT" -w '%{http_code}' --max-time 30 \
    -H 'Content-Type: application/json' \
    -d @"$SAMPLE_DIR/fixtures/chunk-final.json" "$BASE/transcribe")"
  sanitize_file "$OUT"
  EXPECTED_STATUS="$CODE" "$PYBIN" "$SHARED/assert_response.py" \
    "$OUT" "$SAMPLE_DIR/fixtures/expected.json"
  rm -f "$OUT"
  echo "PASS T3 voice-vad-client-server: final chunk response conforms"
fi
