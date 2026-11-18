#!/usr/bin/env sh
# Executes the 12-scenario runtime model cache verification protocol (§4.2) for Security.
set -eu
SAMPLE_DIR="$(cd "$(dirname "$0")/.." && pwd)"
SHARED="$SAMPLE_DIR/../shared/scripts"
. "$SHARED/lib.sh"

"$PYBIN" "$SAMPLE_DIR/tests/test_e2e_acceptance.py" SecuritySampleAcceptanceTests.test_wse2e_at_008_runtime_model_cache_12_scenarios
echo "PASS security 12-scenario runtime model cache matrix"
