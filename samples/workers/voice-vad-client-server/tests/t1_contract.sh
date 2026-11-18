#!/usr/bin/env sh
# T1: offline contract-fixture validation + §6.1 acceptance tests for voice-vad-client-server.
set -eu
HERE="$(cd "$(dirname "$0")/.." && pwd)"
SHARED="$HERE/../shared/scripts"
. "$SHARED/lib.sh"
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/chunk-0.json" text,sequence,isFinal
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/chunk-1.json" text,sequence,isFinal
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/chunk-final.json" text,sequence,isFinal
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/expected.json" expectedStatus,requiredResponseFields
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/vad-modes-matrix.json" schemaVersion,fixtureId,modes,invariants
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/preroll-and-resample-24k-to-16k.json" schemaVersion,fixtureId,capture,target,preRoll,resampleRatio
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/sequence-gaps-and-device-reset.json" schemaVersion,fixtureId,sileroSpecs,events
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/barge-in-epoch-fencing.json" schemaVersion,fixtureId,sessionId,speechEpoch,nextSpeechEpoch,expectedBehavior
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/permission-denied-and-headless.json" schemaVersion,fixtureId,permissionDeniedCase,headlessNodeEnvironment
"$PYBIN" "$HERE/tests/test_vad_client_server.py"
echo "PASS T1 voice-vad-client-server: fixtures + §6.1 acceptance verified"
