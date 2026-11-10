#!/usr/bin/env sh
# T1: offline contract-fixture, negative policy, and cache protocol validation for security.
# No docker, no network. Covers WSE2E-AT-001..008 offline assertions.
set -eu
HERE="$(cd "$(dirname "$0")/.." && pwd)"
SHARED="$HERE/../shared/scripts"
. "$SHARED/lib.sh"

"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/request.json" schemaVersion,requestId
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/expected.json" expectedStatus,requiredResponseFields
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/rules-pii.json" schemaVersion,fixtureId,request,expectedSpans
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/unicode-normalization.json" schemaVersion,fixtureId,cases
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/overlap-priority.json" schemaVersion,fixtureId,actionPriorityOrder,cases
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/prompt-injection.json" schemaVersion,fixtureId,cases
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/malicious-detector-output.json" schemaVersion,fixtureId,cases
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/forged-token.json" schemaVersion,fixtureId,vaultEncryptionAlgorithm,cases
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/../shared/fixtures/worker-envelope.ping.json" schemaVersion,messageId,taskId,kind

test -f "$HERE/fixtures/face-and-ocr.png"
test -f "$HERE/fixtures/unsupported-image.bin"
test -f "$HERE/fixtures/policy-coverage.yaml"

"$PYBIN" "$HERE/tests/test_e2e_acceptance.py"
echo "PASS T1 security: fixtures, policy negatives, and 12-scenario cache matrix valid"
