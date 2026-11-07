# OpenConnect engineering document bundle — v1.1

Read in this order:
1. `spec.md` — product scope, architecture and normative requirements.
2. `configuration.md` — **new** strongly typed .NET enums/options, default detector choices, Interactive/Operational agent contracts.
3. `firewall.md` — Python security worker, synchronous outbound gates, optional detectors and coverage-aware policy.
4. `implementation.md` — staged coding tasks (including IMP-090–099), CI/release execution guidance.
5. `checklist.md` — evidence gates and tests (including AT-031–042); items are unchecked intentionally.

## Key updates
- Privacy firewall remains default-on, but AI detector models are individually disabled by default. Rules-only is the lightweight baseline and is **not semantic PII protection**.
- Interactive agents run in active authenticated user context, reauthorized for tool use and bounded delegation.
- Operational agents run under distinct workload identities, scheduled and monitored by a durable .NET BackgroundService with NATS/outbox/leases.
- All closed options use C# enums and validated wire names; JSON/YAML/Python/TS schema parity is required.
- This bundle contains **design specifications**, not deployed code or verified CI results.
