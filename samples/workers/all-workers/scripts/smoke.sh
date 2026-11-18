#!/usr/bin/env sh
# Aggregate smoke test for all-workers (WSE2E-IMP-023).
# Verifies T0 static and runs T1 isolation/topology verification.
set -eu
HERE="$(cd "$(dirname "$0")/.." && pwd)"
sh "$HERE/tests/t1_contract.sh"
echo "PASS smoke all-workers: aggregate configuration valid"
