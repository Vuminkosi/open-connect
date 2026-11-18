---
title: Privacy firewall
version: v1
summary: Keep privacy and authorization at the trusted .NET host boundary.
order: 20
---

# Privacy firewall

The host privacy firewall is enabled by default. Worker detectors return signals; the .NET host makes the final policy decision.

## Fail closed

When a configured protected egress requires unavailable detector coverage, the host blocks that egress. Optional detector absence must not be advertised as ready protection.

## Boundary rules

Raw sensitive content must not enter NATS messages, telemetry or unauthorized tools. Token vault restoration is scoped to the tenant, principal, session, turn and policy revision.
