#!/usr/bin/env sh
# Collect sanitized evidence for a harness run into shared/evidence/<run-id>/.
# Emits machine-readable result.json (openconnect.samples.workers.evidence/v1) and summary.md (§3 & §7.4).
# Usage: collect-evidence.sh [--run-id <id>] [--tier static|contract|image-startup|integration|t0|t1|t2|t3] [--provider sqlite|postgres|sqlserver|local]
set -eu
HERE="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=lib.sh
. "$HERE/lib.sh"

RUN_ID="$(date -u +%Y%m%dT%H%M%SZ)"
TIER="contract"
PROVIDER="sqlite"
while [ $# -gt 0 ]; do
  reject_destructive_flag "$1"
  case "$1" in
    --run-id) RUN_ID="$2"; shift 2;;
    --tier) TIER="$2"; shift 2;;
    --provider) PROVIDER="$2"; shift 2;;
    -h|--help) echo "Usage: collect-evidence.sh [--run-id <id>] [--tier static|contract|image-startup|integration] [--provider sqlite|postgres|sqlserver|local]"; exit 0;;
    *) echo "error: unknown argument '$1'" >&2; exit 2;;
  esac
done

if [ -z "$RUN_ID" ]; then echo "error: --run-id must not be empty" >&2; exit 2; fi
require_tier "$TIER"
require_provider "$PROVIDER"

NORM_TIER="$(normalize_tier "$TIER")"
OUT="$SHARED_DIR/evidence/$RUN_ID"
mkdir -p "$OUT"
SOURCE_SHA="$(git rev-parse --short HEAD 2>/dev/null || echo 'unknown')"

{
  echo "stamp=$RUN_ID tier=$NORM_TIER provider=$PROVIDER sha=$SOURCE_SHA"
  echo "t4=model-live BLOCKED/NOT_RUN (no approved model assets)"
} > "$OUT/run.txt"

for w in $KNOWN_WORKERS; do
  out="$(mktemp)"
  if sh "$HERE/run-one.sh" "$w" --tier "$TIER" --provider "$PROVIDER" >"$out" 2>&1; then
    echo "RESULT $w: PASS" >> "$out"
  else
    echo "RESULT $w: FAIL-or-BLOCKED (see above)" >> "$out"
  fi
  sanitize_stream < "$out" > "$OUT/$w.log"
  rm -f "$out"
done

"$PYBIN" - "$OUT" "$RUN_ID" "$NORM_TIER" "$PROVIDER" "$SOURCE_SHA" "$SHARED_DIR/traceability.csv" <<'PYEOF'
import csv, json, os, sys
out_dir, run_id, tier, provider, sha, csv_path = sys.argv[1:7]
records = []
if os.path.exists(csv_path):
    with open(csv_path, newline="", encoding="utf-8") as f:
        reader = csv.DictReader(f)
        for row in reader:
            at_id = row["sample_acceptance_id"]
            uri = row["ci_artifacts"]
            worker = uri.split("/")[4] if len(uri.split("/")) > 4 else "security"
            records.append({
                "schemaVersion": "openconnect.samples.workers.evidence/v1",
                "runId": run_id,
                "worker": worker,
                "tier": tier,
                "acceptanceId": at_id,
                "status": "NOT_RUN",
                "sourceCommit": sha,
                "imageDigest": f"local:e2e-{sha}",
                "modelFingerprint": None,
                "databaseProvider": provider,
                "command": f"./samples/workers/shared/scripts/run-one.sh {worker} --tier {tier} --provider {provider}",
                "evidenceUri": uri,
                "reviewId": None,
                "observedAtUtc": None
            })
with open(os.path.join(out_dir, "result.json"), "w", encoding="utf-8") as jf:
    json.dump(records, jf, indent=2)
with open(os.path.join(out_dir, "summary.md"), "w", encoding="utf-8") as mf:
    mf.write(f"# OpenConnect Worker Samples E2E Evidence Summary\n\n")
    mf.write(f"- **Run ID**: `{run_id}`\n- **Source Commit**: `{sha}`\n- **Tier**: `{tier}`\n- **Provider**: `{provider}`\n\n")
    mf.write("| Acceptance ID | Worker | Tier | Provider | Status |\n|---|---|---|---|---|\n")
    for r in records:
        mf.write(f"| `{r['acceptanceId']}` | `{r['worker']}` | `{r['tier']}` | `{r['databaseProvider']}` | `{r['status']}` |\n")
PYEOF

{
  echo "evidence collected: $OUT"
  ls -1 "$OUT"
} | sanitize_stream
