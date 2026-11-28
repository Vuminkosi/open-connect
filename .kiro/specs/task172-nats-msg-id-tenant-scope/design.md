# Design: NATS messaging Nats-Msg-Id tenant scope (#172)

- `BuildMessageId(envelope)` returns `{tenantId}:{eventId}`. It is used as both the in-process dedup key and
  the `messageId` argument to `IJetStreamPublisher.PublishAsync`. `EventId` is already validated as an opaque
  token (ASCII, at most 128 characters) and the tenant id is a ULID, so the id is a valid header value.
- In-process state under one lock: a bounded completed set (`_seenEventIds` + FIFO `_seenOrder`, capacity
  `dedupCapacity`) and an `_inFlight` map of `TaskCompletionSource<bool>` per message id.
- `PublishCoreAsync` loop: completed id returns `false`; an in-flight id is awaited with
  `WaitAsync(cancellationToken)`. If the in-flight attempt succeeded, return `false`. If it failed, loop and
  take ownership. Otherwise the caller owns the attempt, publishes, then records success in the completed set
  (evicting the oldest beyond capacity) or removes the in-flight entry on failure or cancellation and rethrows.
- The in-flight map holds at most one entry per concurrently running publish, so it is bounded by concurrency.
- Public API is unchanged.
