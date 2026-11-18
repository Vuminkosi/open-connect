#!/usr/bin/env sh
# T2 (+T3 when TIER=t3) smoke for Classification. Bounded waits, sanitized logs.
# Exits 3 when the canonical worker Dockerfile is not yet delivered.
set -eu
SAMPLE_DIR="$(cd "$(dirname "$0")/.." && pwd)"
SHARED="$SAMPLE_DIR/../shared/scripts"
# shellcheck source=../../shared/scripts/lib.sh
. "$SHARED/lib.sh"

DOCKERFILE="$SAMPLE_DIR/../../../workers/classification/Dockerfile"
if [ ! -f "$DOCKERFILE" ]; then
  echo "BLOCKED: classification: canonical Dockerfile missing ($DOCKERFILE) pending worker delivery" >&2
  exit 3
fi

BASE="http://127.0.0.1:${PORT_CLASSIFICATION:-8081}"
echo "==> T2 classification: waiting for $BASE/health/ready (bounded ${READY_TIMEOUT_SEC}s)"
wait_ready "$BASE/health/ready"
echo "PASS T2 classification: ready"

if [ "${TIER:-t2}" = "t3" ]; then
  echo "==> T3 classification: execute envelope to /internal/v1/execute (fake engine, synthetic text)"
  TOKEN="${OPENCONNECT_WORKER_TOKEN:-e2e-test-token-do-not-use-in-prod}"
  BODY="$SAMPLE_DIR/fixtures/request.json"
  OUT="$(mktemp "${TMPDIR:-/tmp}/classification-t3-resp.XXXXXX.json")"
  NEG="$(mktemp "${TMPDIR:-/tmp}/classification-t3-neg.XXXXXX.json")"
  trap 'rm -f "$OUT" "$NEG" "$NEG.out"' EXIT INT TERM
  CODE="$(curl -sS -o "$OUT" -w '%{http_code}' --max-time 30 \
    -H 'Content-Type: application/json' \
    -H "Authorization: Bearer $TOKEN" \
    -d @"$BODY" "$BASE/internal/v1/execute")"
  sanitize_file "$OUT"
  EXPECTED_STATUS="$("$PYBIN" -c "import json,sys;print(json.load(open(sys.argv[1]))['expectedStatus'])" "$SAMPLE_DIR/fixtures/expected.json")"
  if [ "$CODE" != "$EXPECTED_STATUS" ]; then
    echo "FAIL T3 classification: expected HTTP $EXPECTED_STATUS, got $CODE" >&2
    exit 1
  fi
  EXPECTED_STATUS="$CODE" "$PYBIN" "$SHARED/assert_response.py" \
    "$OUT" "$SAMPLE_DIR/fixtures/expected.json"
  "$PYBIN" - "$OUT" <<'PYEOF'
import json, sys
body = json.load(open(sys.argv[1]))
assert body.get("status") == "completed", "status must be completed"
result = body.get("result")
assert isinstance(result, dict), "result must be an object"
labels = result.get("labels")
scores = result.get("scores")
assert isinstance(labels, list) and labels, "result.labels must be a non-empty list"
assert isinstance(scores, dict) and scores, "result.scores must be a non-empty object"
assert "abstained" in result, "result.abstained must be present"
assert result.get("engine") == "fake", "fake-engine marker (engine=fake) missing"
assert result.get("engineWarning"), "fake-engine warning missing"
assert result.get("grantsTools") is False, "grantsTools must be False (advisory only)"
print("PASS T3 classification: labels/scores/abstained + fake-engine marker")
PYEOF
  # Negatives: no token -> 401; wrong token -> 401.
  for AUTH in "none" "wrong"; do
    if [ "$AUTH" = "none" ]; then
      NCODE="$(curl -sS -o "$NEG" -w '%{http_code}' --max-time 30 \
        -H 'Content-Type: application/json' \
        -d @"$BODY" "$BASE/internal/v1/execute")"
    else
      NCODE="$(curl -sS -o "$NEG" -w '%{http_code}' --max-time 30 \
        -H 'Content-Type: application/json' \
        -H 'Authorization: Bearer wrong-test-token' \
        -d @"$BODY" "$BASE/internal/v1/execute")"
    fi
    sanitize_file "$NEG"
    if [ "$NCODE" != "401" ]; then
      echo "FAIL T3 classification negative ($AUTH token): expected HTTP 401, got $NCODE" >&2
      exit 1
    fi
  done
  # Negative: unknown labelSetVersion -> 422.
  "$PYBIN" - "$BODY" "$NEG" <<'PYEOF'
import json, sys
doc = json.load(open(sys.argv[1]))
doc["taskId"] = "e2e-classification-t3-neg-labelset"
doc["input"] = dict(doc["input"])
doc["input"]["labelSetVersion"] = "no-such-labelset"
json.dump(doc, open(sys.argv[2], "w"))
PYEOF
  NCODE="$(curl -sS -o "$NEG.out" -w '%{http_code}' --max-time 30 \
    -H 'Content-Type: application/json' \
    -H "Authorization: Bearer $TOKEN" \
    -d @"$NEG" "$BASE/internal/v1/execute")"
  rm -f "$NEG.out"
  sanitize_file "$NEG"
  case "$NCODE" in
    422) echo "PASS T3 classification negatives: 401/422 enforced" ;;
    *) echo "FAIL T3 classification negative (unknown labelSetVersion): expected 422, got $NCODE" >&2; exit 1 ;;
  esac
  rm -f "$OUT" "$NEG"
  trap - EXIT INT TERM
  echo "PASS T3 classification: fixture response conforms"
fi
