#!/bin/sh
set -eu

template=/etc/nats/nats.template.conf
rendered=/etc/nats/nats.conf

[ -r "$template" ] || {
  echo "nats-entrypoint: configuration template is not readable" >&2
  exit 1
}

# Fail before creating a configuration with missing credentials.
: "${NATS_APP_USER:?NATS_APP_USER must be set}"
: "${NATS_APP_PASSWORD:?NATS_APP_PASSWORD must be set}"
: "${NATS_BOOTSTRAP_USER:?NATS_BOOTSTRAP_USER must be set}"
: "${NATS_BOOTSTRAP_PASSWORD:?NATS_BOOTSTRAP_PASSWORD must be set}"

# The rendered file contains credentials; restrict it to the container user.
# Passwords must be alphanumeric for safe sed replacement. If special
# characters are required, use a prebuilt image with a proper renderer.
umask 077
cp "$template" "$rendered"
sed -i "s|\$NATS_APP_USER|${NATS_APP_USER}|g; s|\$NATS_APP_PASSWORD|${NATS_APP_PASSWORD}|g; s|\$NATS_BOOTSTRAP_USER|${NATS_BOOTSTRAP_USER}|g; s|\$NATS_BOOTSTRAP_PASSWORD|${NATS_BOOTSTRAP_PASSWORD}|g" "$rendered"
exec nats-server "$@"
