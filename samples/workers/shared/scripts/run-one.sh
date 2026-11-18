#!/usr/bin/env sh
# POSIX LF-normalized by repository attributes for Linux CI.
# Run a single worker sample across tiers (static, contract, image-startup, integration, model-live, offline-revision, release).
# Usage: run-one.sh [<worker>|--worker <name>] [--tier <tier>] [--provider sqlite|postgres|sqlserver|local] [--manifest <path>] [--retain-on-fail]
# Teardown uses `docker compose down --remove-orphans` (never `down -v`).
set -eu
HERE="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=lib.sh
. "$HERE/lib.sh"

WORKER=""
TIER="integration"
PROVIDER="sqlite"
MANIFEST=""
RETAIN_ON_FAIL=0

while [ $# -gt 0 ]; do
  reject_destructive_flag "$1"
  case "$1" in
    --worker) WORKER="$2"; shift 2;;
    --tier) TIER="$2"; shift 2;;
    --provider) PROVIDER="$2"; shift 2;;
    --manifest) MANIFEST="$2"; shift 2;;
    --retain-on-fail) RETAIN_ON_FAIL=1; shift 1;;
    -h|--help)
      echo "Usage: run-one.sh [<worker>|--worker <name>] [--tier static|contract|image-startup|integration|model-live|offline-revision|release] [--provider sqlite|postgres|sqlserver|local] [--manifest <path>] [--retain-on-fail]"
      exit 0
      ;;
    -*)
      echo "error: unknown argument '$1'" >&2
      exit 2
      ;;
    *)
      if [ -z "$WORKER" ]; then
        WORKER="$1"
        shift 1
      else
        echo "error: unexpected positional argument '$1'" >&2
        exit 2
      fi
      ;;
  esac
done

if [ -z "$WORKER" ]; then echo "error: worker name is required" >&2; exit 2; fi
require_worker "$WORKER"
require_tier "$TIER"
require_provider "$PROVIDER"

NORM_TIER="$(normalize_tier "$TIER")"
SAMPLE_DIR="$SAMPLES_WORKERS_DIR/$WORKER"
COMPOSE_FILE="$SAMPLE_DIR/compose.e2e.yaml"
export COMPOSE_PROJECT_NAME="$(project_name_for "$WORKER")"
export OC_DB_PROVIDER="$PROVIDER"

if [ "$NORM_TIER" = "model-live" ] || [ "$NORM_TIER" = "offline-revision" ] || [ "$NORM_TIER" = "release" ]; then
  if [ -z "$MANIFEST" ] || [ ! -f "$MANIFEST" ]; then
    echo "error: tier '$TIER' ($NORM_TIER) is BLOCKED: approved manifest file required (--manifest <path>); skipping" >&2
    exit 2
  fi
fi

if [ "$NORM_TIER" = "image-startup" ] || [ "$NORM_TIER" = "integration" ]; then
  teardown() {
    rc=$?
    docker compose -f "$COMPOSE_FILE" logs --no-color 2>/dev/null | sanitize_stream | tail -n 50 || true
    if [ "$rc" -ne 0 ] && [ "$RETAIN_ON_FAIL" -eq 1 ]; then
      echo "retain-on-fail enabled for isolated project $COMPOSE_PROJECT_NAME (teardown skipped)" >&2
    else
      docker compose -f "$COMPOSE_FILE" down --remove-orphans 2>&1 | sanitize_stream || true
    fi
  }
  trap teardown EXIT
fi

case "$NORM_TIER" in
  static)
    echo "==> T0 static check: $WORKER (provider=$PROVIDER)"
    docker compose -f "$COMPOSE_FILE" config >/dev/null
    echo "PASS T0 $WORKER: compose config valid"
    ;;
  contract)
    echo "==> T1 contract fixtures: $WORKER (provider=$PROVIDER)"
    sh "$SAMPLE_DIR/tests/t1_contract.sh"
    ;;
  image-startup)
    echo "==> T2 image-startup readiness: $WORKER (provider=$PROVIDER)"
    docker compose -f "$COMPOSE_FILE" up -d --build 2>&1 | sanitize_stream
    TIER="t2" sh "$SAMPLE_DIR/scripts/smoke.sh"
    ;;
  integration)
    echo "==> T3 integration: $WORKER (provider=$PROVIDER)"
    docker compose -f "$COMPOSE_FILE" up -d --build 2>&1 | sanitize_stream
    TIER="t3" sh "$SAMPLE_DIR/scripts/smoke.sh"
    ;;
  model-live|offline-revision|release)
    echo "==> $NORM_TIER: $WORKER (manifest=$MANIFEST provider=$PROVIDER)"
    echo "BLOCKED: live model/release hardware execution requires approved isolated runner; manifest verified at $MANIFEST" >&2
    exit 3
    ;;
esac
