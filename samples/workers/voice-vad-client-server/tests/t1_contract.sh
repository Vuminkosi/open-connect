#!/usr/bin/env sh
# T1: offline contract-fixture validation for voice-vad-client-server.
set -eu
HERE="$(cd "$(dirname "$0")/.." && pwd)"
SHARED="$HERE/../shared/scripts"
. "$SHARED/lib.sh"
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/chunk-0.json" text,sequence,isFinal
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/chunk-1.json" text,sequence,isFinal
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/chunk-final.json" text,sequence,isFinal
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/expected.json" expectedStatus,requiredResponseFields
echo "PASS T1 voice-vad-client-server: fixtures valid"
