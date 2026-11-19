# Issue #60: FirewallGuardedChatClient non-text egress (Spec tier T2: privacy)

## Requirements
- AC-60-1: Every `AIContent` subtype is handled explicitly. Text, text reasoning, function call and function result content are sanitized through `IEgressGate`. All other content (`DataContent`, `UriContent`, unknown subtypes) is denied.
- AC-60-2: Media-only messages do not bypass screening through the empty-text early path.
- AC-60-3: Denial throws `PrivacyBlockedException` (`privacy_blocked`) without echoing content, before any inner-client call, including streaming.
- AC-60-4: Regression tests in `MafFirewallGuardedChatClientTests` use synthetic data only and assert the inner client is not called.
- AC-60-5: No public API change (`PublicAPI.Unshipped.txt` unchanged). Class remarks document screened types.

## Design
`GuardAsync` checks each message first. When the egress destination is `ExternalModel` and any content is not in the screened set, it throws fail-closed. The client has no verified local redaction capability, so `ModalityEgressGate` would deny raw audio and unredacted images/documents for `ExternalModel` in every case. Denying directly gives the same result without a new public constructor dependency. Wiring verified local redaction into the client is deferred.

`TextReasoningContent` text is sanitized and `ProtectedData` preserved. Non-`ExternalModel` destinations keep prior behavior.

## Out of scope
`ChatOptions.Instructions` screening and output inspection (#61).

## Rollback
Revert the commit. Consumers sending multimodal content to external models will be denied by design.
