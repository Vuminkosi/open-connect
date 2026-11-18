#!/usr/bin/env sh
# T2 (+T3 when TIER=t3) smoke for Outbox relay (.NET host). Bounded waits, sanitized logs.
# Exits 3 when the canonical worker Dockerfile is not yet delivered.
#
# Decision REG-S07-DNDESIGN-B: the worker exposes NO ports and has NO
# healthcheck (the former :8080 /health/ready + /relay HTTP refs were
# fictional for this background dispatcher host). This script therefore
# DELEGATES to tests/t3_runtime.sh, which owns the live proof
# (container-running + `Outbox dispatcher started` marker + PUBACK relay +
# duplicate-replica negative + clean teardown). T2 readiness is subsumed by
# that proof; there are no :8080 refs left in this sample's owned scripts.
set -eu
SAMPLE_DIR="$(cd "$(dirname "$0")/.." && pwd)"

DOCKERFILE="$SAMPLE_DIR/../../../workers/outbox/Dockerfile"
if [ ! -f "$DOCKERFILE" ]; then
  echo "BLOCKED: outbox: canonical Dockerfile missing ($DOCKERFILE) pending worker delivery" >&2
  exit 3
fi

echo "==> outbox smoke (TIER=${TIER:-t2}): delegating to tests/t3_runtime.sh (no :8080 surface exists)"
sh "$SAMPLE_DIR/tests/t3_runtime.sh"
echo "PASS outbox smoke: delegated T3 runtime proof complete"
