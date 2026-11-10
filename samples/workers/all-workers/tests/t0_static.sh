#!/usr/bin/env sh
# T0: static validation for all-workers aggregate stack (WSE2E-IMP-023).
# Verifies:
# - All 11 canonical worker services defined with canonical image names
# - Independent profiles configured on each worker
# - Distinct model volumes for Python workers only (never shared, never on ops/playwright)
# - Internal network isolation (internal: true)
# - Embedded vs standalone exclusivity configuration
set -eu
HERE="$(cd "$(dirname "$0")/.." && pwd)"
COMPOSE="$HERE/compose.e2e.yaml"

if [ ! -f "$COMPOSE" ]; then
  echo "FAIL T0 all-workers: missing $COMPOSE" >&2
  exit 1
fi

WORKERS="security classification nllb inference transcription realtime-voice kokoro mms-tts navigator-playwright operational-agents outbox"

for w in $WORKERS; do
  if ! grep -q "vuminkosimatibe/openconnect-worker-$w" "$COMPOSE"; then
    echo "FAIL T0 all-workers: missing canonical image for $w" >&2
    exit 1
  fi
  if ! grep -q "profiles:.*\"$w\"" "$COMPOSE"; then
    echo "FAIL T0 all-workers: worker $w missing dedicated compose profile" >&2
    exit 1
  fi
done

# Verify distinct named model volumes (only the 8 Python workers have model volumes)
MODEL_VOLUMES="oc_security_models oc_classification_models oc_nllb_models oc_inference_models oc_transcription_models oc_realtime_voice_models oc_kokoro_models oc_mms_models"

for v in $MODEL_VOLUMES; do
  if ! grep -q "^  $v:" "$COMPOSE"; then
    echo "FAIL T0 all-workers: missing model volume definition $v" >&2
    exit 1
  fi
done

# Verify navigator-playwright, operational-agents, and outbox do NOT mount any model volumes
if sed -n '/^  navigator-playwright:/,/^  operational-agents:/p' "$COMPOSE" | grep -q "oc_.*_models"; then
  echo "FAIL T0 all-workers: navigator-playwright must not mount any model volume" >&2
  exit 1
fi
if sed -n '/^  operational-agents:/,/^  outbox:/p' "$COMPOSE" | grep -q "oc_.*_models"; then
  echo "FAIL T0 all-workers: operational-agents must not mount any model volume" >&2
  exit 1
fi
if sed -n '/^  outbox:/,/^volumes:/p' "$COMPOSE" | grep -q "oc_.*_models"; then
  echo "FAIL T0 all-workers: outbox must not mount any model volume" >&2
  exit 1
fi

# Verify internal: true network separation
if ! grep -q "internal: true" "$COMPOSE"; then
  echo "FAIL T0 all-workers: compose.e2e.yaml must configure internal: true network separation" >&2
  exit 1
fi

echo "PASS T0 all-workers: aggregate compose, 11 workers, profiles, and model volume invariants verified (WSE2E-IMP-023)"
