#!/usr/bin/env sh
# T1: offline contract-fixture validation + WSE2E-AT-045..051 acceptance tests for kokoro.
set -eu
HERE="$(cd "$(dirname "$0")/.." && pwd)"
SHARED="$HERE/../shared/scripts"
. "$SHARED/lib.sh"
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/request.json" schemaVersion,taskId,kind,payload
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/expected.json" expectedStatus,requiredResponseFields
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/short-text-af-heart.json" schemaVersion,fixtureId,taskId,input,expectedBehavior
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/excessive-text.json" schemaVersion,fixtureId,maxChars,oversizedInput
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/invalid-voice.json" schemaVersion,fixtureId,taskId,input,expectedBehavior
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/empty-text.json" schemaVersion,fixtureId,taskId,input,expectedBehavior
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/interrupted-stream-epoch.json" schemaVersion,fixtureId,taskId,presentedEpoch,currentSessionEpoch,expectedBehavior
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/malformed-markup.json" schemaVersion,fixtureId,taskId,input,expectedBehavior
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/changed-voice-manifest.json" schemaVersion,fixtureId,initialVoices,updatedVoices,expectedBehavior
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/../shared/fixtures/worker-envelope.ping.json" schemaVersion,messageId,taskId,kind
"$PYBIN" "$HERE/tests/test_e2e_acceptance.py"
echo "PASS T1 kokoro: fixtures + WSE2E-AT-045..051 verified"
