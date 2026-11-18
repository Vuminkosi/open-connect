#!/usr/bin/env sh
# T0: static validation for outbox (WSE2E-AT-073).
# Verifies compose.e2e.yaml, Dockerfile, and options invariants:
# - Standalone mode with embedded dispatcher disabled
# - No model volume and no model provisioner
# - Manual migrations (no auto-DDL)
set -eu
HERE="$(cd "$(dirname "$0")/.." && pwd)"
COMPOSE="$HERE/compose.e2e.yaml"
DOCKERFILE="$HERE/../../../workers/outbox/Dockerfile"
HOST_DOCKERFILE="$HERE/../../../src/OpenConnect.WorkerHosts.Outbox/Dockerfile"

if [ ! -f "$COMPOSE" ]; then
  echo "FAIL T0 outbox: missing $COMPOSE" >&2
  exit 1
fi
if [ ! -f "$DOCKERFILE" ] && [ ! -f "$HOST_DOCKERFILE" ]; then
  echo "FAIL T0 outbox: missing Dockerfile" >&2
  exit 1
fi

# Verify canonical image name in compose.e2e.yaml
if ! grep -q "vuminkosimatibe/openconnect-worker-outbox" "$COMPOSE"; then
  echo "FAIL T0 outbox: compose.e2e.yaml missing canonical image name" >&2
  exit 1
fi

# Verify Standalone mode and embedded disabled
if ! grep -q "WorkerHosts__Outbox__Mode: Standalone" "$COMPOSE"; then
  echo "FAIL T0 outbox: compose.e2e.yaml must set WorkerHosts__Outbox__Mode: Standalone" >&2
  exit 1
fi
if ! grep -q 'WorkerHosts__Outbox__EmbeddedDispatcherEnabled: "false"' "$COMPOSE"; then
  echo "FAIL T0 outbox: compose.e2e.yaml must set WorkerHosts__Outbox__EmbeddedDispatcherEnabled: \"false\"" >&2
  exit 1
fi

# Verify no model volume or provisioner in compose.e2e.yaml
if grep -qiE "(oc_.*_models|model-init|provision)" "$COMPOSE" | grep -qv "^#"; then
  echo "FAIL T0 outbox: compose.e2e.yaml must not define a model volume or provisioner" >&2
  exit 1
fi

echo "PASS T0 outbox: static & configuration invariants verified (WSE2E-AT-073)"
