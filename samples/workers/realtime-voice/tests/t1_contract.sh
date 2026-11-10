#!/usr/bin/env sh
# T1: offline contract-fixture validation for realtime-voice. No docker, no network.
set -eu
HERE="$(cd "$(dirname "$0")/.." && pwd)"
SHARED="$HERE/../shared/scripts"
. "$SHARED/lib.sh"
# ExecuteRequest envelope: schemaVersion/taskId/kind/payload (voice.session-open
# + voice.event session flow; real response: schemaVersion/taskId/kind +
# state/speechEpoch[/result]).
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/request.json" schemaVersion,taskId,kind,payload
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/event-begin-listening.json" schemaVersion,taskId,kind,payload
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/event-transcript-final.json" schemaVersion,taskId,kind,payload
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/event-begin-reasoning.json" schemaVersion,taskId,kind,payload
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/event-begin-speaking.json" schemaVersion,taskId,kind,payload
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/event-stale-epoch.json" schemaVersion,taskId,kind,payload
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/expected.json" expectedStatus,requiredResponseFields
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/../shared/fixtures/worker-envelope.ping.json" schemaVersion,messageId,taskId,kind
echo "PASS T1 realtime-voice: fixtures valid"
