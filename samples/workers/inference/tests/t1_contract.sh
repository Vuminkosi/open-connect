#!/usr/bin/env sh
# T1: offline contract-fixture validation + WSE2E-AT-021..028 acceptance tests for inference.
set -eu
HERE="$(cd "$(dirname "$0")/.." && pwd)"
SHARED="$HERE/../shared/scripts"
. "$SHARED/lib.sh"
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/request.json" schemaVersion,requestId
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/expected.json" expectedStatus,requiredResponseFields
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/capabilities-matrix.json" schemaVersion,fixtureId,baseCapabilities,optionalCapabilities,configurations
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/context-overflow.json" schemaVersion,fixtureId,limits,overflowCases
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/structured-output-contract.json" schemaVersion,fixtureId,requestId,jsonSchema,sampleResponse
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/stream-chunk-sequence.json" schemaVersion,fixtureId,requestId,expectedProperties,sampleChunks
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/cancellation-race.json" schemaVersion,fixtureId,taskId,leaseMs,scenarios
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/unsupported-embedding-mode.json" schemaVersion,fixtureId,requestId,requestedCapabilities,expectedBehavior
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/malicious-fake-engine-claim.json" schemaVersion,fixtureId,environment,requestedEngine,expectedRejection
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/provider-network-deny-policy.json" schemaVersion,fixtureId,privacyMode,cloudApiUrl,expectedBehavior
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/../shared/fixtures/worker-envelope.ping.json" schemaVersion,messageId,taskId,kind
"$PYBIN" "$HERE/tests/test_e2e_acceptance.py"
echo "PASS T1 inference: fixtures + WSE2E-AT-021..028 verified"
