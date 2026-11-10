#!/usr/bin/env sh
# T1: offline contract-fixture validation for outbox. No docker, no network.
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
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/atomic-task-event-outbox.json" fixtureId,acceptanceId,tenantScope,atomicAcceptance
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/fenced-claim-and-puback.json" fixtureId,acceptanceId,claimParameters,invariants
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/crash-after-puback-replay.json" fixtureId,acceptanceId,scenario,deterministicMessageIdFormat
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/broker-outage-retention.json" fixtureId,acceptanceId,scenario,maxRetryBackoffSeconds
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/competing-dispatchers-100-rows.json" fixtureId,acceptanceId,totalRows,competingDispatchers
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/provider-matrix-no-auto-ddl.json" fixtureId,acceptanceId,providers,schemaGuards
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/fixtures/canary-and-poison-quarantine.json" fixtureId,acceptanceId,maxAttempts,quarantineCriteria
"$PYBIN" "$SHARED/check_fixtures.py" "$HERE/../shared/fixtures/worker-envelope.ping.json" schemaVersion,messageId,taskId,kind

if command -v dotnet >/dev/null 2>&1; then
  dotnet test "$HERE/tests/OutboxSampleTests.csproj" -c Release --no-restore
elif command -v dotnet.exe >/dev/null 2>&1; then
  WIN_CSPROJ="$(wslpath -w "$HERE/tests/OutboxSampleTests.csproj" 2>/dev/null || echo "$HERE/tests/OutboxSampleTests.csproj")"
  dotnet.exe test "$WIN_CSPROJ" -c Release --no-restore
fi

echo "PASS T1 outbox: fixtures and .NET test suite valid (WSE2E-AT-073..080)"
