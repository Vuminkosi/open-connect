---
title: Troubleshooting and deployment
version: v1
summary: Diagnose common startup, gateway, worker and documentation issues.
order: 100
---

# Troubleshooting

## Documentation does not load

Check `/health/live` and `/health/ready`. The portal's static content does not require a gateway, database, NATS or worker. A readiness failure means the content index did not load.

## Composer shows unavailable

The assistant/composer is optional. Verify the configured gateway URL, HTTPS policy, authentication and `/openconnect/v1/health/ready`. Static documentation should remain available when the gateway is unavailable.

## Worker is unavailable

Check the worker's readiness endpoint and capability manifest. Do not treat a process liveness response as model readiness. Missing required detector coverage must fail closed rather than downgrade privacy.
