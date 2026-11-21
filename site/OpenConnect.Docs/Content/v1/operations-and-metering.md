---
title: Operations, telemetry and budgets
version: v1
summary: Observe execution safely and understand process-local budget limits.
order: 95
---

# Operations, telemetry and budgets

Production operation requires separate checks for the host, database, broker, model provider and each worker. Track readiness and task outcomes, outbox backlog, failures and retries; an HTTP liveness check alone cannot establish that a user request completed. Do not put prompts, tool arguments, audio, documents, tokens or vault mappings in logs or metrics.

## Privacy-aware telemetry

`OpenConnect.Observability` includes `SafeActivityListener` and a default telemetry privacy filter. Configure your application's OpenTelemetry listeners and exporters for the services you actually deploy. Review all custom instrumentation and exporters as well: a listener on one instrumentation path cannot guarantee that unrelated logs or third-party exporters are safe. Use bounded, non-sensitive error categories and correlation identifiers rather than content.

LiveUI exposes the `OpenConnect.LiveUI` meter for manifest, action and receipt timings. These measure the documented software boundaries; they do not prove persistence of an application change or acoustic playback latency. Protect telemetry access and retention as application data.

## Reserve and settle a budget

`OpenConnect.Metering` provides an in-memory guard. For example:

```csharp
using OpenConnect.Metering;

IBudgetGuard guard = new InMemoryBudgetGuard(new Dictionary<string, decimal>
{
    ["support"] = 25.00m,
});
var reservation = guard.TryReserve("support", estimatedAmount: 1.50m);
if (reservation is null)
{
    // Deny or defer the operation; no budget was reserved.
}
else
{
    // After the provider reports the actual cost:
    guard.Settle(reservation, actualCost: 1.25m);
}
```

Reserve before sending billable work. Release a reservation only when you know the work was not sent; reconcile uncertain provider outcomes instead of assuming zero cost. Settlement and release are idempotent for a reservation. Unknown scopes are denied. With the default missing-cost policy, an unknown actual cost is charged at the reserved estimate; `MissingCostPolicy.Deny` also blocks further reservations for that scope. The in-memory guard loses state on restart and cannot enforce a shared multi-node budget: use an appropriate durable adapter for that requirement.

## Failure and recovery

Record the task and operation identifiers needed to reconcile a failure without recording sensitive payloads. Distinguish a request rejected before dispatch, one accepted by the host, a worker receipt and an external side effect. Retry only with the operation's idempotency contract and a bounded policy. Keep migrations, backups, model cache provisioning and credential rotation in deployment procedures appropriate to your environment.

## Related links

- [NATS and worker integration](/docs/v1/nats-and-workers)
- [Troubleshooting and deployment](/docs/v1/troubleshooting)
