#!/usr/bin/env sh
# Executes the 12-scenario runtime model cache lifecycle matrix for workers/kokoro.
set -eu
SAMPLE_DIR="$(cd "$(dirname "$0")/.." && pwd)"
SHARED="$SAMPLE_DIR/../shared/scripts"
. "$SHARED/lib.sh"

echo "==> Running Kokoro TTS worker runtime model cache & acceptance matrix (WSE2E-AT-045..051)"
"$PYBIN" "$SAMPLE_DIR/tests/test_e2e_acceptance.py"
echo "PASS kokoro cache matrix"
