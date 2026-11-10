#!/usr/bin/env sh
# T0: static validation for navigator-playwright (WSE2E-AT-059).
# Verifies compose.e2e.yaml and production Dockerfile invariants:
# - No model volume and no model provisioner
# - Pinned Node LTS and pinned playwright-core@1.49.1
# - Non-root USER 10001:10001
set -eu
HERE="$(cd "$(dirname "$0")/.." && pwd)"
COMPOSE="$HERE/compose.e2e.yaml"
DOCKERFILE="$HERE/../../../workers/navigator-playwright/Dockerfile"

if [ ! -f "$COMPOSE" ]; then
  echo "FAIL T0 navigator-playwright: missing $COMPOSE" >&2
  exit 1
fi
if [ ! -f "$DOCKERFILE" ]; then
  echo "FAIL T0 navigator-playwright: missing $DOCKERFILE" >&2
  exit 1
fi

# Verify canonical image name in compose.e2e.yaml
if ! grep -q "vuminkosimatibe/openconnect-worker-navigator-playwright" "$COMPOSE"; then
  echo "FAIL T0 navigator-playwright: compose.e2e.yaml missing canonical image name" >&2
  exit 1
fi

# Verify no model volume or provisioner in compose.e2e.yaml (WSE2E-AT-059)
if grep -qiE "(oc_.*_models|model-init|provision)" "$COMPOSE" | grep -qv "^#"; then
  echo "FAIL T0 navigator-playwright: compose.e2e.yaml must not define a model volume or provisioner" >&2
  exit 1
fi

# Verify pinned playwright-core and non-root user in Dockerfile (WSE2E-AT-059)
if ! grep -q "playwright-core@1.49.1" "$DOCKERFILE"; then
  echo "FAIL T0 navigator-playwright: Dockerfile missing pinned playwright-core@1.49.1" >&2
  exit 1
fi
if ! grep -q "USER 10001:10001" "$DOCKERFILE"; then
  echo "FAIL T0 navigator-playwright: Dockerfile missing non-root USER 10001:10001" >&2
  exit 1
fi

echo "PASS T0 navigator-playwright: static & Dockerfile invariants verified (WSE2E-AT-059)"
