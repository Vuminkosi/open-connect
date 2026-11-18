#!/usr/bin/env sh
# T1: contract and topology validation for all-workers aggregate stack (WSE2E-IMP-023).
set -eu
HERE="$(cd "$(dirname "$0")/.." && pwd)"
SHARED="$HERE/../shared/scripts"
_LIB_TMP="$(mktemp)"
tr -d '\r' < "$SHARED/lib.sh" > "$_LIB_TMP"
. "$_LIB_TMP"
rm -f "$_LIB_TMP"

sh "$HERE/tests/t0_static.sh"

"$PYBIN" "$HERE/scripts/verify-isolation.py" "$HERE/compose.e2e.yaml"

echo "PASS T1 all-workers: full worker matrix topology and isolation invariants verified"
