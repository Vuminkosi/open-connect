#!/usr/bin/env sh
# Run T0-T3 for a single worker sample.
# Usage: run-one.sh --worker <name> [--tier t0|t1|t2|t3] [--provider local]
# Teardown uses `docker compose down --remove-orphans` (never `down -v`).
set -eu
HERE="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=lib.sh
. "$HERE/lib.sh"

WORKER=""
TIER="t3"
PROVIDER="local"
while [ $# -gt 0 ]; do
  case "$1" in
    --worker) WORKER="$2"; shift 2;;
    --tier) TIER="$2"; shift 2;;
    --provider) PROVIDER="$2"; shift 2;;
    -h|--help) echo "Usage: run-one.sh --worker <name> [--tier t0|t1|t2|t3] [--provider local]"; exit 0;;
    *) echo "error: unknown argument '$1'" >&2; exit 2;;
  esac
done

if [ -z "$WORKER" ]; then echo "error: --worker is required" >&2; exit 2; fi
require_worker "$WORKER"
require_tier "$TIER"
require_provider "$PROVIDER"

SAMPLE_DIR="$SAMPLES_WORKERS_DIR/$WORKER"
COMPOSE_FILE="$SAMPLE_DIR/compose.e2e.yaml"
export COMPOSE_PROJECT_NAME="$(project_name_for "$WORKER")"

if [ "$TIER" = "t2" ] || [ "$TIER" = "t3" ]; then
  # Ordinary teardown must preserve volumes for post-mortem; never `down -v`.
  teardown() {
    docker compose -f "$COMPOSE_FILE" logs --no-color 2>/dev/null | sanitize_stream | tail -n 50 || true
    docker compose -f "$COMPOSE_FILE" down --remove-orphans 2>&1 | sanitize_stream || true
  }
  trap teardown EXIT
fi

case "$TIER" in
  t0)
    echo "==> T0 static check: $WORKER"
    docker compose -f "$COMPOSE_FILE" config >/dev/null
    echo "PASS T0 $WORKER: compose config valid"
    ;;
  t1)
    echo "==> T1 contract fixtures: $WORKER"
    sh "$SAMPLE_DIR/tests/t1_contract.sh"
    ;;
  t2|t3)
    echo "==> T2 readiness: $WORKER"
    docker compose -f "$COMPOSE_FILE" up -d --build 2>&1 | sanitize_stream
    # Per-sample smoke script performs the bounded ready-wait (T2) and,
    # for t3, the fixture-driven request/response check (T3).
    TIER="$TIER" sh "$SAMPLE_DIR/scripts/smoke.sh"
    ;;
esac
