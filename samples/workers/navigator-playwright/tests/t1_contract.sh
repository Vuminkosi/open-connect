#!/usr/bin/env sh
# T1: offline contract-fixture and security policy validation for navigator-playwright.
# Verifies WSE2E-AT-059..065 without requiring Docker or external network access.
set -eu
HERE="$(cd "$(dirname "$0")/.." && pwd)"
SHARED="$HERE/../shared/scripts"
_LIB_TMP="$(mktemp)"
tr -d '\r' < "$SHARED/lib.sh" > "$_LIB_TMP"
. "$_LIB_TMP"
rm -f "$_LIB_TMP"

sh "$HERE/tests/t0_static.sh"

"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/request.json" tool,args
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/expected.json" expectedStatus,requiredResponseFields
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/allowlisted-flow.json" fixtureId,acceptanceId,steps
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/ssrf-and-allowlist-negatives.json" fixtureId,acceptanceId,cases
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/capture-consent-and-redaction.json" fixtureId,acceptanceId,withoutConsent,withConsent
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/cross-tenant-isolation.json" fixtureId,acceptanceId,tenantA,tenantB,invariants
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/crash-timeout-cleanup.json" fixtureId,acceptanceId,excessiveWaitProbe,simulatedCrashProbe
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/worker-absent-fallback.json" fixtureId,acceptanceId,readOnlyTourPlan,browserAutomationProbeWhenWorkerAbsent
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/../shared/fixtures/worker-envelope.ping.json" schemaVersion,messageId,taskId,kind

if command -v node >/dev/null 2>&1; then
  node --test "$HERE/tests/e2e_navigator_suite.mjs"
elif command -v node.exe >/dev/null 2>&1; then
  WIN_SUITE="$(wslpath -w "$HERE/tests/e2e_navigator_suite.mjs" 2>/dev/null || echo "$HERE/tests/e2e_navigator_suite.mjs")"
  node.exe --test "$WIN_SUITE"
fi

echo "PASS T1 navigator-playwright: fixtures and WSE2E-AT-059..065 suite valid"
