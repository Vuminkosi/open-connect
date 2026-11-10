#!/usr/bin/env sh
# Run T0-T3 across all worker samples (and the aggregate all-workers file).
# Continues past failures and reports a summary table. Exit 1 if any fail.
# Usage: run-all.sh [--tier t0|t1|t2|t3] [--provider local]
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
    -h|--help) echo "Usage: run-all.sh [--tier t0|t1|t2|t3] [--provider local]"; exit 0;;
    *) echo "error: unknown argument '$1'" >&2; exit 2;;
  esac
done

require_tier "$TIER"
require_provider "$PROVIDER"

pass=0; fail=0; blocked=0
for w in $KNOWN_WORKERS; do
  out="$(mktemp)"
  if sh "$HERE/run-one.sh" --worker "$w" --tier "$TIER" --provider "$PROVIDER" >"$out" 2>&1; then
    rc=0
  else
    rc=$?
  fi
  sanitize_stream < "$out"
  rm -f "$out"
  if [ "$rc" -eq 0 ]; then
    echo "RESULT $w: PASS"
    pass=$((pass + 1))
  elif [ "$rc" -eq 3 ]; then
    echo "RESULT $w: BLOCKED (pending worker delivery)"
    blocked=$((blocked + 1))
  else
    echo "RESULT $w: FAIL"
    fail=$((fail + 1))
  fi
done

echo "summary: pass=$pass fail=$fail blocked=$blocked tier=$TIER provider=$PROVIDER"
echo "T4 model-live: BLOCKED/NOT_RUN (no approved model assets)."
if [ "$fail" -gt 0 ]; then exit 1; fi
exit 0
