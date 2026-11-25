#!/bin/sh
set -eu

CLOUD_GATEWAY_PORT="${CLOUD_GATEWAY_PORT:-8091}"
export CLOUD_GATEWAY_PORT

START_GATEWAY=1
if [ "${CLOUD_GATEWAY_ENABLED:-true}" = "false" ]; then
    START_GATEWAY=0
fi

START_ASSISTANT=0
if [ "${DOCS_ASSISTANT_HOST_ENABLED:-auto}" != "false" ] \
    && [ "${OpenConnect__Assistant__Enabled:-false}" = "true" ] \
    && [ -n "${OpenConnect__Assistant__Model__Endpoint:-}" ] \
    && [ -f /app/assistant/OpenConnect.Docs.AssistantHost.dll ]; then
    START_ASSISTANT=1
fi

if [ "$START_GATEWAY" -eq 0 ] && [ "$START_ASSISTANT" -eq 0 ]; then
    exec dotnet /app/OpenConnect.Docs.dll
fi

PIDS=""

dotnet /app/OpenConnect.Docs.dll &
DOCS_PID=$!
PIDS="$DOCS_PID"

GATEWAY_PID=""
if [ "$START_GATEWAY" -eq 1 ]; then
    (
        cd /app/gateway
        exec node /app/gateway/start.mjs
    ) &
    GATEWAY_PID=$!
    PIDS="$PIDS $GATEWAY_PID"
fi

ASSISTANT_PID=""
if [ "$START_ASSISTANT" -eq 1 ]; then
    (
        cd /app/assistant
        ASPNETCORE_ENVIRONMENT=Development \
        ASPNETCORE_URLS="${DOCS_ASSISTANT_URLS:-https://127.0.0.1:8098}" \
        exec dotnet /app/assistant/OpenConnect.Docs.AssistantHost.dll
    ) &
    ASSISTANT_PID=$!
    PIDS="$PIDS $ASSISTANT_PID"
fi

STOPPING=0
SLEEP_PID=""

is_alive() {
    kill -0 "$1" 2>/dev/null || return 1
    if [ -r "/proc/$1/stat" ]; then
        case "$(cat "/proc/$1/stat" 2>/dev/null)" in
            *") Z "*) return 1 ;;
        esac
    fi
    return 0
}

all_alive() {
    for pid in $PIDS; do
        if ! is_alive "$pid"; then
            return 1
        fi
    done
    return 0
}

shutdown() {
    SIGNAL="${1:-TERM}"
    if [ "$STOPPING" -eq 1 ]; then
        return
    fi
    STOPPING=1
    for pid in $PIDS; do
        kill "-$SIGNAL" "$pid" 2>/dev/null || true
    done
    if [ -n "$SLEEP_PID" ]; then
        kill "$SLEEP_PID" 2>/dev/null || true
    fi
}

trap 'shutdown TERM' TERM
trap 'shutdown INT' INT

EXIT_CODE=0
while all_alive; do
    sleep 1 &
    SLEEP_PID=$!
    wait "$SLEEP_PID" 2>/dev/null || true
    SLEEP_PID=""
    if [ "$STOPPING" -eq 1 ]; then
        break
    fi
done

if [ "$STOPPING" -eq 0 ]; then
    for pid in $PIDS; do
        if ! is_alive "$pid"; then
            wait "$pid" || EXIT_CODE=$?
        fi
    done
    for pid in $PIDS; do
        kill -TERM "$pid" 2>/dev/null || true
    done
    for pid in $PIDS; do
        wait "$pid" 2>/dev/null || true
    done
else
    for pid in $PIDS; do
        wait "$pid" 2>/dev/null || EXIT_CODE=$?
    done
fi

exit "$EXIT_CODE"
