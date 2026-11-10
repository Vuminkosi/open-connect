#!/usr/bin/env sh
# T1: offline contract-fixture validation for realtime-voice. No docker, no network.
set -eu
HERE="$(cd "$(dirname "$0")/.." && pwd)"
SHARED="$HERE/../shared/scripts"
. "$SHARED/lib.sh"
# request.json follows contracts/workers/v1/speech-chunk.json (no envelope fields).
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/request.json" text,sequence,isFinal
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/expected.json" expectedStatus,requiredResponseFields
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/../shared/fixtures/worker-envelope.ping.json" schemaVersion,messageId,taskId,kind
echo "PASS T1 realtime-voice: fixtures valid"
