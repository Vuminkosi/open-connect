#!/usr/bin/env sh
# Executes the runtime model cache verification protocol (§4.2) for Classification.
set -eu
SAMPLE_DIR="$(cd "$(dirname "$0")/.." && pwd)"
SHARED="$SAMPLE_DIR/../shared/scripts"
. "$SHARED/lib.sh"

"$PYBIN" "$SAMPLE_DIR/tests/test_e2e_acceptance.py" ClassificationSampleAcceptanceTests.test_wse2e_at_014_model_cache_12_scenarios
echo "PASS classification runtime model cache matrix"
