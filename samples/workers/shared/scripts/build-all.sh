#!/usr/bin/env sh
# Build the :e2e-local image(s) for one worker or all workers.
# Local tags only. Never pushes. Never downloads models.
# Usage: build-all.sh [--worker <name>] [--provider local]
set -eu
HERE="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=lib.sh
. "$HERE/lib.sh"

WORKER="all"
PROVIDER="local"
while [ $# -gt 0 ]; do
  case "$1" in
    --worker) WORKER="$2"; shift 2;;
    --provider) PROVIDER="$2"; shift 2;;
    -h|--help) echo "Usage: build-all.sh [--worker <name>|all] [--provider local]"; exit 0;;
    *) echo "error: unknown argument '$1'" >&2; exit 2;;
  esac
done

require_provider "$PROVIDER"
if [ "$WORKER" != "all" ]; then
  require_worker "$WORKER"
  TARGETS="$WORKER"
else
  TARGETS="$KNOWN_WORKERS"
fi

for w in $TARGETS; do
  file="$(compose_for "$w")"
  if [ ! -f "$file" ]; then
    echo "BLOCKED: $w: compose file missing ($file) pending worker delivery" >&2
    continue
  fi
  echo "==> building $w (project $(project_name_for "$w"))"
  COMPOSE_PROJECT_NAME="$(project_name_for "$w")" \
    docker compose -f "$file" build --pull=false 2>&1 | sanitize_stream
done
echo "build-all done (provider=$PROVIDER). T4 model-live: BLOCKED/NOT_RUN."
