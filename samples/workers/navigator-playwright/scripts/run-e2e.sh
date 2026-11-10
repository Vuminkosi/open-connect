#!/usr/bin/env sh
# End-to-end sample runner for navigator-playwright (WSE2E-IMP-018, WSE2E-AT-059..065).
# Supports TIER=t0|t1|t2|t3 (default: t1).
set -eu
SAMPLE_DIR="$(cd "$(dirname "$0")/.." && pwd)"
SHARED="$SAMPLE_DIR/../shared/scripts"
TIER="${TIER:-t1}"

case "$TIER" in
  t0)
    sh "$SAMPLE_DIR/tests/t0_static.sh"
    ;;
  t1)
    sh "$SAMPLE_DIR/tests/t1_contract.sh"
    ;;
  t2|t3)
    sh "$SHARED/run-sample.sh" "navigator-playwright" "$TIER"
    ;;
  *)
    echo "Unknown TIER=$TIER (expected t0|t1|t2|t3)" >&2
    exit 2
    ;;
esac
