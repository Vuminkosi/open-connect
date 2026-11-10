#!/usr/bin/env sh
# Executes the 12-scenario runtime model cache lifecycle matrix for workers/transcription.
set -eu
SAMPLE_DIR="$(cd "$(dirname "$0")/.." && pwd)"
SHARED="$SAMPLE_DIR/../shared/scripts"
. "$SHARED/lib.sh"

echo "==> Running Transcription worker runtime model cache & acceptance matrix (WSE2E-AT-029..035)"
"$PYBIN" "$SAMPLE_DIR/tests/test_e2e_acceptance.py"
echo "PASS transcription cache matrix"
