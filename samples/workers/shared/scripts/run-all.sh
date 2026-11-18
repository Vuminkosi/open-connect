#!/usr/bin/env sh
# Run across all 11 worker samples and report a sanitized summary table.
# Usage: run-all.sh [--tier static|contract|image-startup|integration|t0|t1|t2|t3] [--provider sqlite|postgres|sqlserver|local] [--retain-on-fail]
set -eu
HERE="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=lib.sh
. "$HERE/lib.sh"

TIER="contract"
PROVIDER="sqlite"
EXTRA_ARGS=""
while [ $# -gt 0 ]; do
  reject_destructive_flag "$1"
  case "$1" in
    --tier) TIER="$2"; shift 2;;
    --provider) PROVIDER="$2"; shift 2;;
    --retain-on-fail) EXTRA_ARGS="--retain-on-fail"; shift 1;;
    -h|--help) echo "Usage: run-all.sh [--tier static|contract|image-startup|integration] [--provider sqlite|postgres|sqlserver|local] [--retain-on-fail]"; exit 0;;
    *) echo "error: unknown argument '$1'" >&2; exit 2;;
  esac
done

require_tier "$TIER"
require_provider "$PROVIDER"

pass=0; fail=0; blocked=0
for w in $KNOWN_WORKERS; do
  out="$(mktemp)"
  if [ -n "$EXTRA_ARGS" ]; then
    if sh "$HERE/run-one.sh" "$w" --tier "$TIER" --provider "$PROVIDER" "$EXTRA_ARGS" >"$out" 2>&1; then rc=0; else rc=$?; fi
  else
    if sh "$HERE/run-one.sh" "$w" --tier "$TIER" --provider "$PROVIDER" >"$out" 2>&1; then rc=0; else rc=$?; fi
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
