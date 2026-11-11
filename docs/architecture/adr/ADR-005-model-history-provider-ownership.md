# ADR-005: Model History Provider Ownership

**Status:** Accepted

**Date:** 2026-11-11

**Deciders:** Session Domain Lead, Master Orchestrator (per OC2-D-SESSION-001, AG-REQ-067–082)

## Context

Different AI model providers support conversation history through differing mechanisms:
1. **Native MAF Session / Local History Provider**: The host application owns and manages message transcripts and turn sequences locally, persisting them via `NativeAgentSessionPersistence` / `INativeAgentSessionStore` with optimistic concurrency fences (`NativeAgentSessionBinding`). Standard chat completion endpoints (OpenAI-compatible APIs, Anthropic, Ollama, local models) follow this model.
2. **Service-Managed History**: The upstream model service owns conversation state remotely (e.g. OpenAI Assistants API / Threads, specialized conversational cloud services), where conversation turns and thread history reside authoritatively on the provider's servers.

A dual-ownership configuration where both local MAF session persistence and service-managed thread history claim authority over the same conversation introduces synchronization hazards, race conditions, history duplication, replay vulnerabilities, and privacy firewall bypasses.

## Decision

Every registered provider adapter MUST explicitly document and configure exactly ONE authoritative model history owner:
- Either `ModelHistoryOwner.NativeMafSession` (local host/MAF session store manages transcript and sequence),
- Or `ModelHistoryOwner.ServiceManaged` (remote provider service manages thread history).
- **Never both:** Declaring both or neither is an invalid configuration. The host validation gate (`ModelHistoryOwnershipValidator`) fails closed at registration/startup time.

## Invariants

1. **Single Authority:** A provider execution path consults only its designated history owner. The host never replicates service-managed thread messages into a competing local prompt history, nor does it override local native MAF session state with unverified remote history.
2. **Fail Closed:** If a provider does not declare its authoritative history owner, or declares an unrecognized or contradictory policy, execution fails closed before dispatching any model calls.
3. **Audit Trail:** History ownership policy is immutable per provider descriptor and recorded in audit traces.

## Consequences

- Clear boundary: No duplicate history injectors exist (conforming to `OC2-D-SESSION-011`).
- Deterministic turn replay: Host restart restore paths only activate for `NativeMafSession` providers.
- Strict isolation: Service-managed threads are accessed via their service thread handle without local transcript mutation.
