# Issue 73: domain-neutral voice transcript admission (local prototype)

## Requirements and boundaries

- R1: Without a registered policy, the SDK waits for its existing stream-end/best-final fallback and submits the authoritative accumulated transcript unchanged. Existing provider trimming, longest-segment selection and segment merging remain.
- R2: An optional host DI policy controls only early admission timing and accepted-text normalization. A false early decision is a delay, not a rejection: the fallback remains mandatory. This is not an authorization or filtering API.
- R3: Both Todo sample hosts explicitly opt into the prior Todo early-admission heuristic and substitutions through their existing shared registration.
- R4: Identity, authorization dependencies, session/capture/epoch deduplication, cancellation tokens, provider messages and close/error behavior are unchanged.

## Design and compatibility

Add `IVoiceTranscriptAdmissionPolicy` in Hosting.AspNetCore with two synchronous pure host-owned operations: `ShouldAdmitEarly` and `NormalizeTranscript`. Resolve once per relay from request services; no registration is the neutral default. Pass the resolved instance to the existing private admission method. Keep domain vocabulary in the sample assembly only.

This is additive API but intentionally changes the default SDK behavior for applications that relied on sample-specific substitutions or early admission. Such applications must explicitly register a policy. The Todo sample registration preserves its old behavior. No dependencies or wire-contract changes are needed.

## Failure/security analysis

The policy receives only transcript text; it cannot supply identity, session, capture IDs, dedup keys or cancellation tokens. Its output is task input, never an authorization decision. A custom policy is trusted host code and must return non-null text; failures propagate as existing relay errors rather than silently falling back to a domain heuristic. No new logging, outbound calls or inference. Existing unrelated relay limitations are not modified.

## Verification and rollback

Synthetic in-memory WebSockets exercise the actual relay and fake task dispatcher: neutral delayed admission, unchanged text, custom early/normalization, longest/best-final selection and provider close exception fallback. Sample registration is exercised separately with actual relay admission and no live task execution. Selected in-memory Hosting and sample registration tests, strict build and sorted unique API validation are required. The full Hosting suite (including loopback HTTP), browser and live audio/provider suites are NOT VERIFIED in this no-network functional pass. No live microphone, provider, model, worker, production service or vulnerability reproduction.

Rollback is removal of the added interface/policy wiring and sample registration as one change; no persistence/schema rollback. This local prototype is not a release or merge authorization.

## Local verification evidence

Baseline: `3e5f3e49dd824dcf92300019b53f66b318600198`. SDK 10.0.404, Linux cloud workspace, serial MSBuild (`-m:1 -nr:false`), Release configuration. Dependency setup alone used network access.

- RED: two actual-relay neutral-default regressions failed on the original handler (premature sample-phrase admission and unwanted text substitution).
- PASS: Hosting `VoiceTranscriptAdmissionTests`, 9 passed, 0 failed/skipped. Covers neutral timing/text, custom timing/identity/transformation, exactly-once admission across fallback, longest provider text, graceful/exception close fallback, partial-only stream and cancellation propagation.
- PASS: Docs `TodoVoiceAdmissionTests`, 4 passed, 0 failed/skipped. Actual shared sample registration and in-memory relay/queue with fake executor verify old early/late admission and substitutions, no duplicate fallback, preserved tenant/principal/session, and custom pre-registration precedence.
- PASS: locked restores for both test projects; Hosting library Release warning-as-error build across net8.0/net9.0/net10.0 (0 warnings/errors); scoped whitespace verification; all 34 API baseline files sorted/unique; whitespace diff check.
- NOT VERIFIED: browser voice-to-Todo acceptance, real audio/provider/worker paths, full Hosting/Docs suites, full solution, hosted CI and runtime tests on net8.0/net9.0. These are outside this local, no-network-functional-test pass. No issue closure or release is claimed.
