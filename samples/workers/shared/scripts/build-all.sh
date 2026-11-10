#!/usr/bin/env sh
# Build the local E2E image(s) for one worker or all workers.
# Local tags only. Never pushes. Never downloads models.
# Usage: build-all.sh [--tag <tag>] [--worker <name>] [--provider sqlite|postgres|sqlserver|local]
set -eu
HERE="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=lib.sh
. "$HERE/lib.sh"

WORKER="all"
PROVIDER="sqlite"
IMAGE_TAG="e2e-local"
while [ $# -gt 0 ]; do
  reject_destructive_flag "$1"
  case "$1" in
    --tag) IMAGE_TAG="$2"; shift 2;;
    --worker) WORKER="$2"; shift 2;;
    --provider) PROVIDER="$2"; shift 2;;
    -h|--help) echo "Usage: build-all.sh [--tag <tag>] [--worker <name>|all] [--provider sqlite|postgres|sqlserver|local]"; exit 0;;
    -*) echo "error: unknown argument '$1'" >&2; exit 2;;
    *) WORKER="$1"; shift 1;;
  esac
done

if [ -z "$IMAGE_TAG" ]; then echo "error: --tag must not be empty" >&2; exit 2; fi
require_provider "$PROVIDER"
if [ "$WORKER" != "all" ]; then
  require_worker "$WORKER"
  TARGETS="$WORKER"
else
  TARGETS="$KNOWN_WORKERS"
fi

export E2E_IMAGE_TAG="$IMAGE_TAG"
for w in $TARGETS; do
  file="$(compose_for "$w")"
  if [ ! -f "$file" ]; then
    echo "BLOCKED: $w: compose file missing ($file) pending worker delivery" >&2
    continue
  fi
  echo "==> building $w (project $(project_name_for "$w"), tag=$E2E_IMAGE_TAG)"
  COMPOSE_PROJECT_NAME="$(project_name_for "$w")" \
    docker compose -f "$file" build --pull=false 2>&1 | sanitize_stream
done
echo "build-all done (tag=$E2E_IMAGE_TAG provider=$PROVIDER). T4 model-live: BLOCKED/NOT_RUN."
