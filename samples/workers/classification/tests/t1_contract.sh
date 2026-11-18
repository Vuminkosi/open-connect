#!/usr/bin/env sh
# T1: offline contract-fixture, advisory policy, and cache validation for classification.
# No docker, no network. Covers WSE2E-AT-009..014 offline assertions.
set -eu
HERE="$(cd "$(dirname "$0")/.." && pwd)"
SHARED="$HERE/../shared/scripts"
. "$SHARED/lib.sh"

"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/request.json" schemaVersion,taskId,input
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/expected.json" expectedStatus,requiredResponseFields
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/label-sets.json" schemaVersion,fixtureId,defaultLabelSet,labelSets
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/balanced-sentences.json" schemaVersion,fixtureId,cases
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/conflicting-labels.json" schemaVersion,fixtureId,cases
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/out-of-distribution.json" schemaVersion,fixtureId,cases
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/invalid-scores-and-schema.json" schemaVersion,fixtureId,invalidRequests,invalidWorkerOutputs
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/forged-tool-grant.json" schemaVersion,fixtureId,forgedResponses
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/../shared/fixtures/worker-envelope.ping.json" schemaVersion,messageId,taskId,kind

"$PYBIN" "$HERE/tests/test_e2e_acceptance.py"
echo "PASS T1 classification: fixtures, advisory-only policy, and cache matrix valid"
