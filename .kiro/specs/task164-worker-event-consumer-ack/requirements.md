# Task 164: WorkerEventConsumer acknowledges only after handler success

Issue: #164. Tier: T1.

## Problem

`WorkerEventConsumer.DispatchAsync` recorded `(tenantId, eventId)` as processed before running the handler.
If the handler threw or was cancelled, the event stayed marked and every redelivery returned `false`, so the
event was lost. A concurrent duplicate of an in-flight event also returned `false` before the first attempt
had succeeded, which let a transport acknowledge a delivery whose handler later failed.

## Acceptance criteria

| ID | Criterion | Code | Tests |
| --- | --- | --- | --- |
| AC-1 | A handler exception or cancellation leaves the event redeliverable, and redelivery runs the handler. | `WorkerEventConsumer.RunAsync` (`finally` removes the entry on failure) | `Ac1_*` |
| AC-2 | A concurrent duplicate of an in-flight event does not return `false` before the first attempt succeeds. If the first attempt fails, the waiter runs the handler and returns `true`. | `DispatchAsync` wait loop on `Entry.Completion` | `Ac2_*`, `WaiterCancellation_*` |
| AC-3 | A duplicate of a successfully processed event returns `false` and the handler runs once. | `DispatchAsync` | `A12_WorkerEventConsumer_DeduplicatesRepeatedDeliveries`, `Ac2_ConcurrentDuplicate_DoesNotReturnFalseUntilFirstSucceeds` |
| AC-4 | Eviction and process-local scope are documented in XML docs, and eviction is tested. In-flight entries are never evicted. | `TrimIfNeeded`, class remarks | `Ac4_*` |
| AC-5 | The default registration without a handler is documented as a no-op. | `AddOpenConnectMessaging` remarks, constructor docs | `Ac5_NullHandler_*` |
| AC-6 | The public API surface is unchanged and the build passes with `-warnaserror`. | `PublicAPI.Unshipped.txt` untouched | Release build |

## Out of scope

Cross-restart and multi-replica deduplication (durable store), and the NATS publisher (#172).
