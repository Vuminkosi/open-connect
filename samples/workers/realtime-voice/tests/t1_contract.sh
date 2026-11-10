#!/usr/bin/env sh
# T1: offline contract-fixture validation + WSE2E-AT-036..044 acceptance tests for realtime-voice.
set -eu
HERE="$(cd "$(dirname "$0")/.." && pwd)"
SHARED="$HERE/../shared/scripts"
. "$SHARED/lib.sh"
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/request.json" schemaVersion,taskId,kind,payload
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/event-begin-listening.json" schemaVersion,taskId,kind,payload
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/event-transcript-final.json" schemaVersion,taskId,kind,payload
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/event-begin-reasoning.json" schemaVersion,taskId,kind,payload
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/event-begin-speaking.json" schemaVersion,taskId,kind,payload
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/event-stale-epoch.json" schemaVersion,taskId,kind,payload
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/five-turns-session.json" schemaVersion,fixtureId,sessionId,turns
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/barge-in-interruption.json" schemaVersion,fixtureId,sessionId,initialEpoch,bargeInEvent,expectedBehavior
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/no-speech-session.json" schemaVersion,fixtureId,sessionId,idleTimeoutSec,expectedBehavior
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/resample-24k-to-16k.json" schemaVersion,fixtureId,sourceAudio,sourceSampleRateHz,targetAudio,targetSampleRateHz
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/silero-512-sequence-and-gaps.json" schemaVersion,fixtureId,sileroFrameSize,contextSize,recurrentStateSize,cases
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/duplicate-reordered-headers.json" schemaVersion,fixtureId,cases
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/tts-playback-echo.json" schemaVersion,fixtureId,echoCancellationEnabled,ttsPlaybackBufferMs,expectedBehavior
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/disconnect-reconnect-and-cross-tenant.json" schemaVersion,fixtureId,tenantA,tenantB,sessionA,sessionB,expectedBehavior
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/expected.json" expectedStatus,requiredResponseFields
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/../shared/fixtures/worker-envelope.ping.json" schemaVersion,messageId,taskId,kind
"$PYBIN" "$HERE/tests/test_e2e_acceptance.py"
echo "PASS T1 realtime-voice: fixtures + WSE2E-AT-036..044 verified"
