# Requirements: NATS messaging Nats-Msg-Id tenant scope (#172)

Spec tier: T2 (transport deduplication identity, upgrade note).

## Problem

`NatsMessagingEventPublisher` deduplicated in-process on `{tenantId}:{eventId}` but passed only
`EventId` to `IJetStreamPublisher` (sent as `Nats-Msg-Id`). JetStream deduplicates per stream on that
header regardless of subject, so two tenants reusing an `EventId` caused silent loss. A concurrent
duplicate also returned `false` while the first publish was still in flight, even if it later failed.

## Acceptance criteria

- AC-1: The message id passed to `IJetStreamPublisher` differs for equal `EventId` across tenants.
- AC-2: The message id is stable for retries of the same tenant event.
- AC-3: A concurrent duplicate dispatch does not return `false` before the first attempt succeeded. If the
  first attempt fails, the waiting caller performs its own publish.
- AC-4: The message id satisfies NATS header and length constraints (ASCII letters, digits, `-`, `_`, `:`;
  at most tenant id length + 1 + 128 characters).
- AC-5: XML docs state the dedup scope; CHANGELOG has an upgrade note.
- AC-6: Public signatures are unchanged.

## Compatibility and rollback

The broker dedup identity changes from `{eventId}` to `{tenantId}:{eventId}`. Events published by an older
version inside the stream duplicate window are not recognised as duplicates of a re-publish by the new
version, so a single redelivery is possible during upgrade. Rollback is reverting the change; no data
migration is involved.
