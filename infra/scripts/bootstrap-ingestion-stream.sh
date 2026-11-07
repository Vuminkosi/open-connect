#!/bin/sh
set -eu
: "${NATS_URL:?NATS_URL is required}"
: "${NATS_BOOTSTRAP_USER:?Set NATS_BOOTSTRAP_USER in infra/.env}"
: "${NATS_BOOTSTRAP_PASSWORD:?Set NATS_BOOTSTRAP_PASSWORD in infra/.env}"
: "${NATS_INGESTION_STREAM:=OC_INGESTION}"
: "${NATS_INGESTION_SUBJECT:=oc.v1.ingestion.>}"
set -- --server "$NATS_URL" --user "$NATS_BOOTSTRAP_USER" --password "$NATS_BOOTSTRAP_PASSWORD"
if [ -n "${OPENCONNECT_NATS_OPTIONS_FILE:-}" ] && [ -f "$OPENCONNECT_NATS_OPTIONS_FILE" ]; then
  set -- "$@" --config "$OPENCONNECT_NATS_OPTIONS_FILE"
fi
nats "$@" stream add "$NATS_INGESTION_STREAM" --subjects "$NATS_INGESTION_SUBJECT" --storage file --retention limits --defaults
