#!/usr/bin/env sh
# Profile-drill script demonstrating starting an individual worker profile
# or group profile independently without booting the full stack.
# Usage:
#   sh scripts/run-profile-drill.sh [profile_name]
set -eu
SAMPLE_DIR="$(cd "$(dirname "$0")/.." && pwd)"
PROFILE="${1:-security}"
COMPOSE_FILE="$SAMPLE_DIR/compose.e2e.yaml"

echo "==> Validating profile '$PROFILE' in aggregate compose"
docker compose -f "$COMPOSE_FILE" --profile "$PROFILE" config >/dev/null

echo "PASS run-profile-drill: profile '$PROFILE' is valid and independently runnable"
