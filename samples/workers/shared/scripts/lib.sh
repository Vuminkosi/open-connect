#!/usr/bin/env sh
# Shared helpers for samples/workers e2e harness (T0-T6 / static..release).
# No helper here may download models at build time, push images in PR, or use secrets.
set -eu

KNOWN_WORKERS="security classification nllb inference transcription realtime-voice kokoro mms-tts navigator-playwright operational-agents outbox"
KNOWN_EXTRA_SAMPLES="voice-vad-client-server all-workers"
KNOWN_TIERS="static contract image-startup integration model-live offline-revision release t0 t1 t2 t3 t4 t5 t6"
KNOWN_PROVIDERS="sqlite postgres sqlserver local"

SAMPLES_WORKERS_DIR="$(cd "$(dirname "$0")/../.." && pwd)"
SHARED_DIR="$(cd "$(dirname "$0")/.." && pwd)"

READY_TIMEOUT_SEC="${READY_TIMEOUT_SEC:-120}"
READY_POLL_SEC="${READY_POLL_SEC:-2}"

PYBIN=""
for _pycand in python3 python; do
  if command -v "$_pycand" >/dev/null 2>&1 && "$_pycand" -c "pass" >/dev/null 2>&1; then
    PYBIN="$(command -v "$_pycand")"
    break
  fi
done
unset _pycand
if [ -z "$PYBIN" ]; then
  echo "error: no working python3/python on PATH (required for fixture checks)" >&2
  exit 2
fi

is_known_worker() { case " $KNOWN_WORKERS " in *" $1 "*) return 0;; *) return 1;; esac; }
is_known_sample() { case " $KNOWN_WORKERS $KNOWN_EXTRA_SAMPLES " in *" $1 "*) return 0;; *) return 1;; esac; }
is_known_tier() { case " $KNOWN_TIERS " in *" $1 "*) return 0;; *) return 1;; esac; }
is_known_provider() { case " $KNOWN_PROVIDERS " in *" $1 "*) return 0;; *) return 1;; esac; }

reject_destructive_flag() {
  case "${1:-}" in
    -v|--volumes|--rmi|--force-rm|--prune)
      echo "error: destructive flag '${1}' is forbidden by harness policy" >&2
      exit 2
      ;;
  esac
}

normalize_tier() {
  case "${1:-}" in
    t0|static) printf 'static';;
    t1|contract) printf 'contract';;
    t2|image-startup) printf 'image-startup';;
    t3|integration) printf 'integration';;
    t4|model-live) printf 'model-live';;
    t5|offline-revision) printf 'offline-revision';;
    t6|release) printf 'release';;
    *) printf '%s' "${1:-}";;
  esac
}

require_worker() {
  if ! is_known_sample "${1:-}"; then
    echo "error: unknown worker/sample '${1:-}' (expected one of: $KNOWN_WORKERS $KNOWN_EXTRA_SAMPLES)" >&2
    exit 2
  fi
}

require_tier() {
  if ! is_known_tier "${1:-}"; then
    echo "error: unknown tier '${1:-}' (expected one of: $KNOWN_TIERS)" >&2
    exit 2
  fi
}

require_provider() {
  if ! is_known_provider "${1:-}"; then
    echo "error: unknown provider '${1:-}' (expected one of: $KNOWN_PROVIDERS)" >&2
    exit 2
  fi
}

project_name_for() {
  printf 'ocw-e2e-%s' "$1"
}

sanitize_stream() {
  sed -e 's/\([Bb]earer \)[^ "]*$/\1***REDACTED***/g' \
      -e 's/\([Bb]earer \)[^ "]* /\1***REDACTED*** /g' \
      -e 's/\("token" *: *\)"[^"]*"/\1"***REDACTED***"/g' \
      -e 's/\("api[_-]\?key" *: *\)"[^"]*"/\1"***REDACTED***"/g' \
      -e 's/\(OPENCONNECT_SCAN_TOKEN=\)[^ ]*/\1***REDACTED***/g'
}

sanitize_file() {
  tmp="$1.tmp.$$"
  sanitize_stream < "$1" > "$tmp"
  mv "$tmp" "$1"
}

wait_ready() {
  url="$1"
  timeout="${2:-$READY_TIMEOUT_SEC}"
  i=0
  while [ "$i" -lt "$timeout" ]; do
    if curl -fsS --max-time 5 "$url" >/dev/null 2>&1; then
      return 0
    fi
    sleep "$READY_POLL_SEC"
    i=$((i + READY_POLL_SEC))
  done
  echo "error: timed out after ${timeout}s waiting for $url" >&2
  return 1
}

compose_for() {
  printf '%s' "$SAMPLES_WORKERS_DIR/$1/compose.e2e.yaml"
}
