# OpenConnect.Navigator.Operational

Durable operational navigator: leased job execution with an operational handler for
long-running operations work (`OpenConnect.Navigator.Operational`,
`OpenConnect.Navigator.Operational.Durable`).

## Install

```bash
dotnet add package OpenConnect.Navigator.Operational
```

Ships `net8.0`, `net9.0`, and `net10.0` assets; `net10.0` is the default for new consumers.

## How to use

```csharp
using OpenConnect.Navigator.Operational;

OperationalReceipt receipt = await handler.HandleAsync(job, cancellationToken);
```

Implement `IOperationalHandler.HandleAsync` (`OperationalContracts.cs:7`) to run one
`OperationalJob` (`OperationalJob.cs:4`) and return an `OperationalReceipt` (`:17`).
Lease with `IOperationalLeaseStore.TryAcquireAsync` (`OperationalContracts.cs:14`); the
package creates no scheduler — the host supervisor owns scheduling.

`CompletedSteps` counts only steps confirmed successful under a current lease.
If the executor is cancelled or throws after being invoked but before confirming
a result, the run receipt is `Uncertain` and retains previously confirmed progress.
A caller cancelling its own wait still receives cancellation.

Only completed receipts are eligible for durable storage and replay.
`Uncertain` does not establish that a step did not run. It does not prevent a later
request using the same idempotency key from executing again.

## License

Same terms as the OpenConnect repository.
