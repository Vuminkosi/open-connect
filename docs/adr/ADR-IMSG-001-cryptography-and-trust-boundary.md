# ADR-IMSG-001: Cryptography, RFC 9420 MLS Evaluation, and Trust Boundary

- **Status:** Accepted (Phase 0 Cryptographic Release Gate)
- **Date:** 2026-11-25
- **Scope:** OpenConnect IMessaging (`OpenConnect.Abstractions`, `OpenConnect.Core`, `OpenConnect.IMessaging`, `workers/imessaging`, `packages/client-ts`, `samples/Mobile`)

## 1. Context

OpenConnect IMessaging requires end-to-end encrypted (E2EE) human-to-human, human-to-agent, and human-inclusive group messaging across:
- .NET 8/9/10 server and client libraries (`OpenConnect.Core`, `OpenConnect.Client`)
- .NET MAUI Blazor Hybrid mobile clients (Android P0, iOS P1)
- Browser clients (`packages/client-ts`, `packages/composer`)
- A private Python delivery/signaling/push worker (`workers/imessaging`)

The specification mandates a strict cryptographic release gate before advertising `messaging.e2ee`:
- No custom ratchets, homemade key derivation, static tenant AES keys, or mock encryption.
- No TLS-only transport masquerading as E2EE.
- Explicit separation between `PrivateE2EE`, `AgentE2EE`, and `Governed` security profiles.
- Honest threat boundary disclosure.

## 2. Candidate Evaluation: RFC 9420 MLS & OpenMLS

### 2.1 Protocol Selection: RFC 9420 Messaging Layer Security (MLS)

RFC 9420 MLS is selected as the target group key-agreement and message-protection protocol because it provides:
- Asynchronous group key agreement via published, single-use `KeyPackage` objects.
- Forward secrecy (FS) and post-compromise security (PCS) via TreeKEM epoch commits.
- Authenticated membership state and transcript hash verification, preventing silent participant injection or key substitution.
- Standardized ciphersuites (e.g., `MLS_128_DHKEMX25519_AES128GCM_SHA256_Ed25519` and `MLS_128_DHKEMP256_AES128GCM_SHA256_P256`).

### 2.2 Implementation Evaluation: OpenMLS

| Criterion | Assessment | Status / Decision |
|---|---|---|
| License | MIT (permissive, compatible with OpenConnect) | Pass |
| Maintenance & Audit | Active Rust implementation of RFC 9420; independent security audits published on core crate | Pass |
| Browser WASM | Compiles to `wasm32-unknown-unknown` with `openmls_rust_crypto` | Pass (requires deterministic WASM build pipeline) |
| Android / iOS Native | Compiles via Rust `cdylib`/`staticlib` + UniFFI or C-ABI P/Invoke for .NET MAUI | Conditional (requires cross-compilation toolchain in CI) |
| .NET 8/9/10 Interop | No official pure-.NET RFC 9420 MLS library exists; requires native binding behind `IMessagingCryptoProvider` | Blocker for single-commit pure-managed build; mitigated via seam |

### 2.3 Phase 0 Decision & Interop Seam

Because bundling cross-compiled Rust native binaries for `win-x64`, `linux-x64`, `osx-arm64`, `android-arm64`, and `wasm32` requires dedicated native-build CI lanes not yet provisioned in this repository:

1. **Cryptographic Provider Seam (`IMessagingCryptoProvider`)**: All domain coordinators and clients program against `IMessagingCryptoProvider`, decoupling epoch/commit/key-package orchestration from the underlying cryptographic engine.
2. **Phase 0/3 Standards-Based Envelope Baseline**: Until the OpenMLS native/WASM binding pipeline is verified across all targets, `MessagingCryptoEnvelopeService` provides a standards-based authenticated envelope using NIST/IETF primitives (`ECDH P-256` / `X25519`, `HKDF-SHA256` per RFC 5869, `AES-256-GCM` per NIST SP 800-38D, `ECDSA-P256` / `Ed25519` device proof-of-possession) with explicit epoch rekeying on membership change, single-use key-package consumption, and deterministic interop test vectors.
3. **Production E2EE Release Gate**: The `messaging.e2ee` capability MUST NOT be advertised as RFC 9420 MLS-certified until:
   - OpenMLS native/WASM bindings pass cross-platform interop test vectors (Welcome, Commit, Proposal, Application Message, epoch race, device removal).
   - An independent security review validates key custody, crash-safe state persistence, and transcript verification.

## 3. Security Profiles (Immutable Per Conversation)

Once created, a conversation's security profile is **immutable**. Changing profiles requires creating a new conversation with explicit user consent. Silent downgrade from E2EE is strictly forbidden.

| Profile | Participants | Server / Worker Plaintext | Agent Key Custody | Server Content Scanning |
|---|---|---|---|---|
| `PrivateE2EE` | Verified human devices only (no agents allowed) | **Never** (ciphertext only in gateway, DB, NATS, worker) | N/A (adding an agent is rejected) | **None** (not possible; never claimed) |
| `AgentE2EE` | Verified human devices + explicitly authorized agent crypto endpoint | **Never** in delivery worker, NATS, or generic DB; plaintext only inside authorized agent endpoint | Isolated tenant-controlled protected keystore scoped per `(tenant, agent, device, conversation)` | Allowed **only** at authorized agent endpoint via existing OpenConnect Privacy Firewall |
| `Governed` | Organizationally managed encryption (disabled by default) | Permitted per tenant retention/compliance policy | Organizationally managed | Permitted per tenant policy |

## 4. Interoperability Vectors

All implementations (`.NET`, `TypeScript/WASM`, `Python` envelope verification) MUST validate against deterministic vectors covering:
1. **Device Identity Proof-of-Possession**: Signed credential binding `(tenantId, applicationId, subjectId, deviceId, publicKey)`.
2. **KeyPackage Consumption**: Single-use consumption; replay of a consumed KeyPackage is rejected (`409 Conflict`).
3. **Group Epoch Commit & Rekey**:
   - Added device receives new epoch key and **cannot** decrypt prior epochs by default.
   - Removed/revoked device **cannot** decrypt subsequent epochs.
   - Stale epoch commit is rejected (`409 Conflict`) and forces client resync.
4. **Tamper & Replay Rejection**: Modified ciphertext, AAD (`tenantId`, `conversationId`, `epoch`, `sequence`, `senderDeviceId`), or nonce/counter replay fails closed.

## 5. Honest Threat Boundary Disclosure

1. **Metadata is not hidden**: Gateway, database, NATS, and `workers/imessaging` observe tenant IDs, opaque conversation IDs, participant membership lists, message timestamps, sequence numbers, and ciphertext sizes.
2. **Compromised authorized endpoint**: E2EE protects content in transit and at rest across delivery/storage infrastructure, but cannot protect against a compromised human device or a compromised authorized agent endpoint.
3. **Web client code integrity**: Browser clients served by a malicious or compromised gateway cannot defend against modified JavaScript unless an out-of-band code verification mechanism is used.
4. **Cloud inference boundary**: For `AgentE2EE`, forwarding decrypted agent prompts or audio to external cloud inference/STT/TTS providers expands the trust boundary and must be explicitly authorized or blocked by OpenConnect privacy policy.
5. **Python worker zero-plaintext invariant**: `workers/imessaging` NEVER holds participant private keys, NEVER decrypts message payloads, and NEVER accepts plaintext chat content in its execution envelopes or push wake-up payloads.
6. **Voice media boundary**: Existing WSS/TLS duplex voice (`DuplexVoiceSessionCoordinator`) is transport-encrypted unless media encryption terminates inside an explicitly authorized agent endpoint. Calls requiring E2EE MUST fail closed on transport-only media backends.
