#!/bin/sh
set -eu
: "${NATS_URL:?NATS_URL is required}"
: "${NATS_BOOTSTRAP_USER:?Set NATS_BOOTSTRAP_USER in infra/.env}"
: "${NATS_BOOTSTRAP_PASSWORD:?Set NATS_BOOTSTRAP_PASSWORD in infra/.env}"
: "${NATS_NOTIFICATION_STREAM:=OC_NOTIFICATIONS}"
: "${NATS_NOTIFICATION_SUBJECT:=oc.v1.notifications.inapp}"
set -- --server "$NATS_URL" --user "$NATS_BOOTSTRAP_USER" --password "$NATS_BOOTSTRAP_PASSWORD"
if [ -n "${OPENCONNECT_NATS_OPTIONS_FILE:-}" ] && [ -f "$OPENCONNECT_NATS_OPTIONS_FILE" ]; then
  set -- "$@" --config "$OPENCONNECT_NATS_OPTIONS_FILE"
fi
nats "$@" stream add "$NATS_NOTIFICATION_STREAM" --subjects "$NATS_NOTIFICATION_SUBJECT" --storage file --retention limits --defaults
