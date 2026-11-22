#!/bin/sh
set -eu

if [ "${CLOUD_GATEWAY_ENABLED:-true}" = "false" ]; then
    exec dotnet /app/OpenConnect.Docs.dll
fi

CLOUD_GATEWAY_PORT="${CLOUD_GATEWAY_PORT:-8091}"
export CLOUD_GATEWAY_PORT

dotnet /app/OpenConnect.Docs.dll &
DOCS_PID=$!

(
    cd /app/gateway
    exec node /app/gateway/start.mjs
) &
GATEWAY_PID=$!

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

shutdown() {
    SIGNAL="${1:-TERM}"
    if [ "$STOPPING" -eq 1 ]; then
        return
    fi
    STOPPING=1
    kill "-$SIGNAL" "$DOCS_PID" "$GATEWAY_PID" 2>/dev/null || true
    if [ -n "$SLEEP_PID" ]; then
        kill "$SLEEP_PID" 2>/dev/null || true
    fi
}

trap 'shutdown TERM' TERM
trap 'shutdown INT' INT

EXIT_CODE=0
while is_alive "$DOCS_PID" && is_alive "$GATEWAY_PID"; do
    sleep 1 &
    SLEEP_PID=$!
    wait "$SLEEP_PID" 2>/dev/null || true
    SLEEP_PID=""
    if [ "$STOPPING" -eq 1 ]; then
        break
    fi
done

if [ "$STOPPING" -eq 0 ]; then
    if ! is_alive "$DOCS_PID"; then
        wait "$DOCS_PID" || EXIT_CODE=$?
        kill -TERM "$GATEWAY_PID" 2>/dev/null || true
        wait "$GATEWAY_PID" 2>/dev/null || true
    else
        wait "$GATEWAY_PID" || EXIT_CODE=$?
        kill -TERM "$DOCS_PID" 2>/dev/null || true
        wait "$DOCS_PID" 2>/dev/null || true
    fi
else
    wait "$DOCS_PID" 2>/dev/null || EXIT_CODE=$?
    wait "$GATEWAY_PID" 2>/dev/null || true
fi

exit "$EXIT_CODE"
