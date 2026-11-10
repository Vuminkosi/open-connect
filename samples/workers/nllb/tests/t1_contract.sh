#!/usr/bin/env sh
# T1: offline contract-fixture validation + WSE2E-AT-015..020 acceptance tests for nllb.
set -eu
HERE="$(cd "$(dirname "$0")/.." && pwd)"
SHARED="$HERE/../shared/scripts"
. "$SHARED/lib.sh"
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/request.json" schemaVersion,taskId,input
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/expected.json" expectedStatus,requiredResponseFields
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/utf8-normalization.json" schemaVersion,fixtureId,cases
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/supported-language-pairs.json" schemaVersion,fixtureId,supportedLanguages,sampleRequests
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/unsupported-pair.json" schemaVersion,fixtureId,cases
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/empty-and-long-text.json" schemaVersion,fixtureId,maxTextChars,emptyCase,oversizedCase
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/injected-tool-command.json" schemaVersion,fixtureId,injectedText,expectedBehavior
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/corrupt-tokenizer.json" schemaVersion,fixtureId,validManifest,expectedReadiness
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/missing-model-license.json" schemaVersion,fixtureId,cases
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/remote-fallback-trap.json" schemaVersion,fixtureId,input,expectedOfflineBehavior
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/../shared/fixtures/worker-envelope.ping.json" schemaVersion,messageId,taskId,kind
"$PYBIN" "$HERE/tests/test_e2e_acceptance.py"
echo "PASS T1 nllb: fixtures + WSE2E-AT-015..020 verified"
