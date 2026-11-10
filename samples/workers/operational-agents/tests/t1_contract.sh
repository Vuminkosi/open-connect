#!/usr/bin/env sh
# T1: offline contract-fixture validation for operational-agents. No docker, no network.
set -eu
HERE="$(cd "$(dirname "$0")/.." && pwd)"
SHARED="$HERE/../shared/scripts"
_LIB_TMP="$(mktemp)"
tr -d '\r' < "$SHARED/lib.sh" > "$_LIB_TMP"
. "$_LIB_TMP"
rm -f "$_LIB_TMP"

sh "$HERE/tests/t0_static.sh"

"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/request.json" schemaVersion,requestId
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/expected.json" expectedStatus,requiredResponseFields
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/workload-identity-job.json" fixtureId,acceptanceId,tenantScope,validJob
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/competing-hosts-lease-fence.json" fixtureId,acceptanceId,tenantScope,supervisorResourceKey
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/checkpoint-hitl-resume.json" fixtureId,acceptanceId,tenantScope,sessionKey,approvalGate
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/misfire-overlap-revocation-cancel.json" fixtureId,acceptanceId,scheduleValidation,scenarios
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/provider-matrix-and-schema-guard.json" fixtureId,acceptanceId,providers,schemaGuards
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/embedded-standalone-exclusivity.json" fixtureId,acceptanceId,standaloneWithEmbeddedEnabledTrue
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/../shared/fixtures/worker-envelope.ping.json" schemaVersion,messageId,taskId,kind

if command -v dotnet >/dev/null 2>&1; then
  dotnet test "$HERE/tests/OperationalAgentsSampleTests.csproj" -c Release --no-restore
elif command -v dotnet.exe >/dev/null 2>&1; then
  WIN_CSPROJ="$(wslpath -w "$HERE/tests/OperationalAgentsSampleTests.csproj" 2>/dev/null || echo "$HERE/tests/OperationalAgentsSampleTests.csproj")"
  dotnet.exe test "$WIN_CSPROJ" -c Release --no-restore
fi

echo "PASS T1 operational-agents: fixtures and .NET test suite valid (WSE2E-AT-066..072)"
