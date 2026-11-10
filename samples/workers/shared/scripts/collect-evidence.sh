#!/usr/bin/env sh
# Collect sanitized evidence for a harness run into shared/evidence/<stamp>/.
# Never stores tokens, keys, or raw Authorization headers (see lib.sh).
# Usage: collect-evidence.sh [--tier t0|t1|t2|t3] [--provider local]
set -eu
HERE="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=lib.sh
. "$HERE/lib.sh"

TIER="t1"
PROVIDER="local"
while [ $# -gt 0 ]; do
  case "$1" in
    --tier) TIER="$2"; shift 2;;
    --provider) PROVIDER="$2"; shift 2;;
    -h|--help) echo "Usage: collect-evidence.sh [--tier t0|t1|t2|t3] [--provider local]"; exit 0;;
    *) echo "error: unknown argument '$1'" >&2; exit 2;;
  esac
done
require_tier "$TIER"
require_provider "$PROVIDER"

STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
OUT="$SHARED_DIR/evidence/$STAMP"
mkdir -p "$OUT"

{
  echo "stamp=$STAMP tier=$TIER provider=$PROVIDER"
  echo "t4=model-live BLOCKED/NOT_RUN (no approved model assets)"
} > "$OUT/run.txt"

for w in $KNOWN_WORKERS; do
  out="$(mktemp)"
  if sh "$HERE/run-one.sh" --worker "$w" --tier "$TIER" --provider "$PROVIDER" >"$out" 2>&1; then
    echo "RESULT $w: PASS" >> "$out"
  else
    echo "RESULT $w: FAIL-or-BLOCKED (see above)" >> "$out"
  fi
  sanitize_stream < "$out" > "$OUT/$w.log"
  rm -f "$out"
done

{
  echo "evidence collected: $OUT"
  ls -1 "$OUT"
} | sanitize_stream
