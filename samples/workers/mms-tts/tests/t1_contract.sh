#!/usr/bin/env sh
# T1: offline contract-fixture validation + WSE2E-AT-052..058 acceptance tests for mms-tts.
set -eu
HERE="$(cd "$(dirname "$0")/.." && pwd)"
SHARED="$HERE/../shared/scripts"
. "$SHARED/lib.sh"
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/request.json" schemaVersion,taskId,kind,payload
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/expected.json" expectedStatus,requiredResponseFields
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/valid-languages.json" schemaVersion,fixtureId,optInLanguages,cases
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/unapproved-language.json" schemaVersion,fixtureId,optInLanguages,requestedLanguage,text,expectedBehavior
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/invalid-voice-and-code.json" schemaVersion,fixtureId,cases
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/noncommercial-under-commercial-policy.json" schemaVersion,fixtureId,commercialPolicy,checkpointLicense,language,expectedBehavior
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/text-overflow.json" schemaVersion,fixtureId,maxChars,oversizedInput
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/two-approved-revisions.json" schemaVersion,fixtureId,revisions,maxRamCacheEntries,expectedBehavior
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/tokenizer-mismatch.json" schemaVersion,fixtureId,declaredTokenizerHash,actualTokenizerHash,expectedBehavior
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/../shared/fixtures/worker-envelope.ping.json" schemaVersion,messageId,taskId,kind
"$PYBIN" "$HERE/tests/test_e2e_acceptance.py"
echo "PASS T1 mms-tts: fixtures + WSE2E-AT-052..058 verified"
