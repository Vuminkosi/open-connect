#!/usr/bin/env sh
# T1: offline contract-fixture validation + WSE2E-AT-029..035 acceptance tests for transcription.
set -eu
HERE="$(cd "$(dirname "$0")/.." && pwd)"
SHARED="$HERE/../shared/scripts"
. "$SHARED/lib.sh"
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/request.json" schemaVersion,requestId
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/expected.json" expectedStatus,requiredResponseFields
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/silence-request.json" schemaVersion,fixtureId,taskId,audioFile,expectedBehavior
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/known-phrase-request.json" schemaVersion,fixtureId,taskId,audioFile,expectedBehavior
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/unsupported-codec.json" schemaVersion,fixtureId,taskId,mimeType,expectedBehavior
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/oversized-audio-bomb.json" schemaVersion,fixtureId,limits,bombPayload
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/corrupt-container.json" schemaVersion,fixtureId,taskId,audioFile,expectedBehavior
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/unexpected-channels.json" schemaVersion,fixtureId,taskId,declaredChannels,expectedBehavior
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/wrong-sample-rate.json" schemaVersion,fixtureId,taskId,declaredSampleRateHz,expectedBehavior
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/remote-stt-strict-local.json" schemaVersion,fixtureId,privacyMode,cloudApiUrl,expectedBehavior
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/stream-cutoff-partials.json" schemaVersion,fixtureId,taskId,chunks,expectedBehavior
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/../shared/fixtures/worker-envelope.ping.json" schemaVersion,messageId,taskId,kind
"$PYBIN" "$HERE/tests/test_e2e_acceptance.py"
echo "PASS T1 transcription: fixtures + WSE2E-AT-029..035 verified"
