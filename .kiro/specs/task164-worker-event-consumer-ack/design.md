# Design: ack after handler success

## State

`ConcurrentDictionary<string, Entry>` keyed by `"{tenantId}:{eventId}"`. `Entry` holds a
`TaskCompletionSource<bool>` created with `RunContinuationsAsynchronously` and the completion timestamp.
`ConcurrentQueue<string>` records the order in which entries completed.

## Flow

1. Validate the envelope (unchanged, `MessagingDomainException` with `InvalidRequest`).
2. Loop: `TryAdd` a new in-flight entry.
   - Added: run the handler. On success, stamp the time, enqueue the key, trim, complete the entry with `true`, return `true`.
     On any exception (including `OperationCanceledException`), remove the entry (key and value match) and then complete it with `false`, and rethrow.
   - Not added: await the existing entry with `WaitAsync(cancellationToken)`. `true` means it completed earlier, so return `false`.
     `false` means the first attempt failed and released the key, so loop and try to become the owner.
3. A `null` handler is a no-op that commits immediately and returns `true`.

Removal happens before the entry is completed, so a waiter that wakes up always finds the key free.

## Eviction

Only completed entries are queued. Trimming dequeues while `_insertionOrder.Count > maxDedupEntries`, so
in-flight entries are never evicted and the dictionary can exceed the bound only by the number of in-flight events.

## Compatibility

Public signatures are unchanged. Behavior change: handler failures now leave the event redeliverable
and concurrent duplicates wait instead of returning `false`. Callers that treated `false` as "safe to ack"
now get it only after a success.

## Durability and scope

State is process-local and bounded. It is not shared across restarts or replicas, so handlers must be idempotent.
A durable store is a separate piece of work.

## Rollback

Revert the commit. No data or configuration migration is involved.
