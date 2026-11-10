#!/usr/bin/env sh
# T3 runtime proof for the Outbox relay (.NET host) worker.
# Decision REG-S07-DNDESIGN-B: container-running + log marker + PUBACK relay
# asserts, duplicate-replica negative, NO worker code changes.
#
# What it proves (live, against disposable compose postgres + NATS JetStream):
#   1. postgres healthy (pg_isready), NATS healthy (http://localhost:8222/healthz),
#      and sample-owned EF migrations applied BEFORE the worker starts
#      (the worker never runs DDL).
#   2. NATS connected (JetStream stream created), lease held, and the
#      dispatcher container stays running and logs
#      `Outbox dispatcher started … subject oc.v1.notifications.inapp`.
#   3. One seeded pending outbox entry (entry.Id == envelope MessageId) is
#      relayed end to end within 30s: a JetStream consumer read receives it
#      AND the row transitions pending -> delivered (PUBACK-then-marked-delivered),
#      with `Outbox relay delivered 1 entries` in the log.
#   4. A duplicate replica on the same tenant fails closed: it logs
#      `outbox_duplicate_supervisor`, never logs `Outbox dispatcher started`,
#      and the first dispatcher keeps the lease (no takeover).
#   5. Teardown is `down --remove-orphans` (never `down -v`).
#
# Migrator (repo-consistent, no new product code): `dotnet ef migrations script`
# (existing sample-owned migrations in samples/PostgreSql, history
# __PostgreSqlMigrationsHistory) piped into the compose postgres psql BEFORE
# the worker starts. `database update` is NOT used: the checked-in Initial
# migration drifts from the current product model and the repo escalates that
# warning to an error, while `migrations script` renders the owned artifacts
# verbatim.
#
# Synthetic tenant ULID + test password only. Never reads infra/.env.
set -eu

SAMPLE_DIR="$(cd "$(dirname "$0")/.." && pwd)"
REPO_ROOT="$(cd "$SAMPLE_DIR/../../.." && pwd)"
COMPOSE_FILE="$SAMPLE_DIR/compose.e2e.yaml"

# Disposable, project-scoped defaults (overridable, never secrets).
export OC_OUTBOX_TENANT_ULID="${OC_OUTBOX_TENANT_ULID:-01ARZ3NDEKTSV4RRFFQ69G5FAV}"
export POSTGRES_DB="${POSTGRES_DB:-openconnect}"
export POSTGRES_USER="${POSTGRES_USER:-openconnect}"
export POSTGRES_PASSWORD="${POSTGRES_PASSWORD:-e2e-test-pw-do-not-use-in-prod}"
export POSTGRES_PORT="${POSTGRES_PORT:-55434}"
export NATS_HTTP_PORT="${NATS_HTTP_PORT:-8222}"
export NATS_NOTIFICATION_SUBJECT="${NATS_NOTIFICATION_SUBJECT:-oc.v1.notifications.inapp}"
export COMPOSE_PROJECT_NAME="${COMPOSE_PROJECT_NAME:-ocw-outbox-t3-$$}"

# Python for envelope/seed generation only. Probe execution (not just PATH)
# because some systems carry non-functional shims (e.g. a Microsoft Store stub).
PYBIN=""
for _pycand in python3 python; do
  if command -v "$_pycand" >/dev/null 2>&1 && "$_pycand" -c "pass" >/dev/null 2>&1; then
    PYBIN="$(command -v "$_pycand")"
    break
  fi
done
unset _pycand
if [ -z "$PYBIN" ]; then
  echo "FAIL T3 outbox: no working python3/python on PATH (required for seed generation)" >&2
  exit 2
fi

STREAM="OUTBOX_T3"
NATS_SRV="nats://nats:4222"

compose() {
  docker compose -f "$COMPOSE_FILE" -p "$COMPOSE_PROJECT_NAME" "$@"
}

fail() {
  echo "FAIL T3 outbox: $1" >&2
  compose logs --no-color outbox 2>/dev/null | tail -n 30 >&2 || true
  exit 1
}

# nats-box runs on the compose network so `nats` resolves over compose DNS.
natsbox() {
  docker run --rm --network "${COMPOSE_PROJECT_NAME}_default" natsio/nats-box:0.19.7 \
    nats -s "$NATS_SRV" "$@"
}

echo "==> T3 outbox: up --build -d (project $COMPOSE_PROJECT_NAME)"
compose up --build -d || fail "compose up failed"

echo "==> T3 outbox: waiting for postgres + nats healthy"
i=0
while [ "$i" -lt 60 ]; do
  if compose exec -T postgres pg_isready -U "$POSTGRES_USER" -d "$POSTGRES_DB" >/dev/null 2>&1 \
    && compose exec -T nats wget -q -O /dev/null http://localhost:8222/healthz >/dev/null 2>&1; then
    echo "PASS T3 outbox: postgres healthy (pg_isready) + nats healthy (http://localhost:8222/healthz)"
    break
  fi
  sleep 2
  i=$((i + 2))
done
if [ "$i" -ge 60 ]; then fail "postgres+nats never became healthy"; fi

# Sample-owned migrator (repo-consistent, no new product code): render SQL from
# the existing sample-owned EF migrations (samples/PostgreSql, history
# __PostgreSqlMigrationsHistory) and apply it to the disposable compose
# postgres BEFORE the worker starts (the worker never runs DDL).
# NOTE: `dotnet ef database update` is intentionally NOT used: the checked-in
# Initial migration legitimately drifts from the current product model
# (PendingModelChangesWarning is escalated to an error repo-wide), while
# `migrations script` renders the owned artifacts verbatim.
echo "==> T3 outbox: applying sample-owned EF migrations (worker never runs DDL)"
MIG_SQL="$(mktemp)"
# Plain (non-locked) restore rewrites samples/PostgreSql/packages.lock.json
# (its locked targets are stale); back it up and put it back so the harness
# never dirties files outside this sample.
LOCK_BAK="$(mktemp)"
cp "$REPO_ROOT/samples/PostgreSql/packages.lock.json" "$LOCK_BAK"
(
  cd "$REPO_ROOT"
  dotnet restore samples/PostgreSql/OpenConnect.Samples.PostgreSql.csproj >/dev/null
  dotnet ef migrations script --context PostgreSqlSampleDbContext \
    --project samples/PostgreSql/OpenConnect.Samples.PostgreSql.csproj \
    --startup-project samples/PostgreSql/OpenConnect.Samples.PostgreSql.csproj \
    --output "$MIG_SQL"
) || { rm -f "$MIG_SQL" "$LOCK_BAK"; fail "dotnet ef migrations script failed"; }
cp "$LOCK_BAK" "$REPO_ROOT/samples/PostgreSql/packages.lock.json"
rm -f "$LOCK_BAK"
compose exec -T postgres psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" < "$MIG_SQL" >/dev/null \
  || { rm -f "$MIG_SQL"; fail "applying sample migrations to disposable postgres failed"; }
rm -f "$MIG_SQL"
echo "PASS T3 outbox: migrations applied"

echo "==> T3 outbox: creating JetStream stream $STREAM for $NATS_NOTIFICATION_SUBJECT"
natsbox stream add "$STREAM" --subjects "$NATS_NOTIFICATION_SUBJECT" --storage memory \
  --retention limits --discard old --dupe-window 2m --defaults >/dev/null \
  || fail "JetStream stream creation failed"
echo "PASS T3 outbox: JetStream stream $STREAM ready (NATS connected)"

echo "==> T3 outbox: starting dispatcher against migrated schema"
compose up -d outbox >/dev/null || fail "dispatcher start failed"

echo "==> T3 outbox: dispatcher running + start marker (bounded 90s)"
i=0
while [ "$i" -lt 90 ]; do
  if compose logs --no-color outbox 2>/dev/null | grep -q "Outbox dispatcher started"; then
    break
  fi
  sleep 3
  i=$((i + 3))
done
if [ "$i" -ge 90 ]; then fail "log marker 'Outbox dispatcher started' not found"; fi
STATE="$(docker inspect -f '{{.State.Status}}' "$COMPOSE_PROJECT_NAME-outbox-1" 2>/dev/null || echo missing)"
[ "$STATE" = "running" ] || fail "dispatcher container status is '$STATE', expected 'running'"
compose logs --no-color outbox 2>/dev/null | grep "Outbox dispatcher started" | grep -q "subject $NATS_NOTIFICATION_SUBJECT" \
  || fail "start marker missing subject $NATS_NOTIFICATION_SUBJECT"
echo "PASS T3 outbox: dispatcher running + log marker 'Outbox dispatcher started … subject $NATS_NOTIFICATION_SUBJECT' present (lease held)"

echo "==> T3 outbox: seeding 1 pending entry (entry.Id == envelope MessageId)"
SEED_SQL="$(mktemp)"
ENTRY_ID="$("$PYBIN" - "$SEED_SQL" <<'PYEOF'
import json, sys, uuid
from datetime import datetime, timedelta, timezone
entry_id = uuid.uuid4()
lease_id = uuid.uuid4()
now = datetime.now(timezone.utc).replace(microsecond=0)
envelope = {
    "schemaVersion": "openconnect.v1",
    "messageId": str(entry_id),
    "taskId": "e2e-outbox-t3",
    "kind": "notification.inapp",
    "issuedAt": now.isoformat(),
    "expiresAt": (now + timedelta(minutes=10)).isoformat(),
    "lease": {"leaseId": str(lease_id), "fencingToken": 1},
    "payloadRef": "e2e/ref/outbox-t3",
}
payload = json.dumps(envelope).replace("'", "''")
sql = (
    "INSERT INTO \"openconnect\".\"Outbox\""
    " (\"Id\",\"TenantScope\",\"EventId\",\"Subject\",\"SanitizedPayload\",\"Status\","
    "  \"FencingVersion\",\"CreatedUtc\",\"NextAttemptUtc\",\"Attempts\") VALUES "
    f"('{entry_id}','D','{entry_id}','oc.v1.notifications.inapp','{payload}','pending',"
    "  0, now(), now(), 0);"
)
open(sys.argv[1], "w", encoding="utf-8").write(sql + "\n")
print(str(entry_id))
PYEOF
)"
compose exec -T postgres psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -v ON_ERROR_STOP=1 < "$SEED_SQL" >/dev/null \
  || { rm -f "$SEED_SQL"; fail "seeding pending outbox entry failed"; }
rm -f "$SEED_SQL"
echo "PASS T3 outbox: seeded pending entry $ENTRY_ID"

echo "==> T3 outbox: relay within 30s (PUBACK-then-marked-delivered)"
i=0
STATUS=""
while [ "$i" -lt 30 ]; do
  STATUS="$(compose exec -T postgres psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -tAc \
    "SELECT \"Status\" FROM \"openconnect\".\"Outbox\" WHERE \"Id\"='$ENTRY_ID';" \
    | tr -d '[:space:]')"
  if [ "$STATUS" = "delivered" ]; then
    break
  fi
  sleep 2
  i=$((i + 2))
done
[ "$STATUS" = "delivered" ] || fail "entry $ENTRY_ID status is '$STATUS', expected 'delivered' within 30s"
echo "PASS T3 outbox: entry $ENTRY_ID pending -> delivered (PUBACK-then-marked-delivered)"
compose logs --no-color outbox 2>/dev/null | grep -q "Outbox relay delivered 1 entries" \
  || fail "log marker 'Outbox relay delivered 1 entries' not found"
echo "PASS T3 outbox: log marker 'Outbox relay delivered 1 entries' present"

echo "==> T3 outbox: JetStream consumer receipt proof"
MSG_OUT="$(mktemp)"
natsbox stream get "$STREAM" 1 > "$MSG_OUT" 2>&1 || { rm -f "$MSG_OUT"; fail "JetStream stream get failed"; }
grep -q "$ENTRY_ID" "$MSG_OUT" || { rm -f "$MSG_OUT"; fail "JetStream message 1 does not carry entry $ENTRY_ID"; }
rm -f "$MSG_OUT"
echo "PASS T3 outbox: JetStream consumer received entry $ENTRY_ID on $NATS_NOTIFICATION_SUBJECT"

echo "==> T3 outbox: lease-row assert (oc/outbox/supervisor)"
FENCING="$(compose exec -T postgres psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -tAc \
  "SELECT \"FencingVersion\" FROM \"openconnect\".\"Leases\" WHERE \"ResourceKey\"='oc/outbox/supervisor';" \
  | tr -d '[:space:]')"
case "$FENCING" in
  ''|*[!0-9]*) fail "lease row missing or unreadable (got '$FENCING')" ;;
esac
[ "$FENCING" -ge 1 ] || fail "fencing_version=$FENCING, expected >= 1"
echo "PASS T3 outbox: lease row resource_key='oc/outbox/supervisor' fencing_version=$FENCING"

echo "==> T3 outbox: duplicate-replica negative (same tenant must fail closed)"
# Fail-closed proof: the replica refuses with outbox_duplicate_supervisor and
# never logs 'Outbox dispatcher started'; the first dispatcher keeps the
# lease afterwards (re-asserted below).
# NOTE on exit codes (documented variance, same as the op-agents T3 pattern):
# the generic host uses BackgroundServiceExceptionBehavior=StopHost, so the
# duplicate logs crit + shuts down gracefully with container exit 0. Worker
# code is frozen (REG-S07-DNDESIGN-B: no worker changes), so the harness
# asserts fail-closed on the marker + no-takeover instead of the code.
set +e
DUP_OUT="$(compose run --rm --no-deps outbox 2>&1)"
set -e
echo "$DUP_OUT" | grep -q "outbox_duplicate_supervisor" \
  || fail "duplicate replica output missing 'outbox_duplicate_supervisor'"
echo "$DUP_OUT" | grep -q "Outbox dispatcher started" \
  && fail "duplicate replica started dispatching; expected refusal" || true
echo "PASS T3 outbox: duplicate replica refused with 'outbox_duplicate_supervisor'"
echo "==> T3 outbox: first dispatcher still holds the lease (no takeover)"
sleep 12
STATE="$(docker inspect -f '{{.State.Status}}' "$COMPOSE_PROJECT_NAME-outbox-1" 2>/dev/null || echo missing)"
[ "$STATE" = "running" ] || fail "first dispatcher status is '$STATE' after duplicate attempt"
FENCING2="$(compose exec -T postgres psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -tAc \
  "SELECT \"FencingVersion\" FROM \"openconnect\".\"Leases\" WHERE \"ResourceKey\"='oc/outbox/supervisor';" \
  | tr -d '[:space:]')"
[ "$FENCING2" = "$FENCING" ] || fail "fencing_version changed $FENCING -> $FENCING2 (takeover?)"
echo "PASS T3 outbox: first dispatcher retained lease fencing_version=$FENCING2"

echo "==> T3 outbox: teardown down --remove-orphans (never -v)"
compose down --remove-orphans || fail "teardown failed"
echo "PASS T3 outbox: teardown clean"
echo "PASS T3 outbox: runtime proof complete"
