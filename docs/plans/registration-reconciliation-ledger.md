# OpenConnect Registration Reconciliation Ledger

Per `docs/plans/openconnect-registration-spec.md` §15.1, this ledger documents the actual-vs-target API reconciliation across the OpenConnect .NET solution.

## 1. Actual vs Target API Matrix

| TargetName | ExistingName | Namespace | Assembly | PinnedVersion | CompileEvidence | Decision |
|---|---|---|---|---|---|---|
| `AddOpenConnect` | `AddOpenConnect` | `Microsoft.Extensions.DependencyInjection` | `OpenConnect.Core` | 1.0.0-preview | `dotnet build -c Release -warnaserror` (PASS) | reuse |
| `AddOpenConnectPrivacy` | `AddOpenConnectPrivacy` / `EnsureHostPrivacyServices` | `Microsoft.Extensions.DependencyInjection` / `OpenConnect.Privacy` | `OpenConnect.Core` / `OpenConnect.Privacy` | 1.0.0-preview | `dotnet build -c Release -warnaserror` (PASS) | implement (single host firewall, no duplicate instances) |
| `AddOpenConnectObservability` | `AddOpenConnectObservability` | `Microsoft.Extensions.DependencyInjection` | `OpenConnect.Core` | 1.0.0-preview | `dotnet build -c Release -warnaserror` (PASS) | implement |
| `AddOpenConnectModelProvider` | `AddOpenConnectModelProvider` | `Microsoft.Extensions.DependencyInjection` | `OpenConnect.Core` | 1.0.0-preview | `dotnet build -c Release -warnaserror` (PASS) | implement |
| `AddOpenConnectExecutionProfiles` | `AddOpenConnectExecutionProfiles` | `Microsoft.Extensions.DependencyInjection` | `OpenConnect.Core` | 1.0.0-preview | `dotnet build -c Release -warnaserror` (PASS) | implement |
| `AddOpenConnectEntityFramework<TDbContext>` | `AddOpenConnectEntityFramework<TContext>` | `OpenConnect.Persistence.EFCore.DependencyInjection` | `OpenConnect.Persistence.EFCore` | 1.0.0-preview | `dotnet build -c Release -warnaserror` (PASS) | reuse (marker-based DI, manual migrations only, no auto-DDL) |
| `modelBuilder.ApplyOpenConnectModel()` | `ApplyOpenConnectModel` | `OpenConnect.Persistence.EFCore.Model` | `OpenConnect.Persistence.EFCore` | 1.0.0-preview | `dotnet build -c Release -warnaserror` (PASS) | reuse |
| `AddOpenConnectNats` | `AddOpenConnectNats` | `Microsoft.Extensions.DependencyInjection` | `OpenConnect.Core` / `OpenConnect.Transport.Nats` | 1.0.0-preview | `dotnet build -c Release -warnaserror` (PASS) | implement |
| `AddOpenConnectLeadAgent` | `AddOpenConnectLeadAgent` | `OpenConnect.Agents.Templates` / `Microsoft.Extensions.DependencyInjection` | `OpenConnect.Agents` / `OpenConnect.Core` | 1.0.0-preview | `dotnet build -c Release -warnaserror` (PASS) | reuse + implement alias (single lead invariant enforced) |
| `AddOpenConnectAgent` | `AddOpenConnectAgent` | `OpenConnect.Agents.Templates` / `Microsoft.Extensions.DependencyInjection` | `OpenConnect.Agents` / `OpenConnect.Core` | 1.0.0-preview | `dotnet build -c Release -warnaserror` (PASS) | reuse |
| `AddOpenConnectTool<TTool>` | `AddOpenConnectTool<TTool>` | `Microsoft.Extensions.DependencyInjection` | `OpenConnect.Core` | 1.0.0-preview | `dotnet build -c Release -warnaserror` (PASS) | implement |
| `AddOpenConnectMcp` | `AddOpenConnectMcp` | `Microsoft.Extensions.DependencyInjection` | `OpenConnect.Core` | 1.0.0-preview | `dotnet build -c Release -warnaserror` (PASS) | implement |
| `AddOpenConnectMcpService` | `AddOpenConnectMcpService` | `Microsoft.Extensions.DependencyInjection` | `OpenConnect.Core` | 1.0.0-preview | `dotnet build -c Release -warnaserror` (PASS) | implement |
| `AddOpenConnectVoice` | `AddOpenConnectVoice` | `Microsoft.Extensions.DependencyInjection` | `OpenConnect.Core` | 1.0.0-preview | `dotnet build -c Release -warnaserror` (PASS) | implement |
| `AddOpenConnectServerVadSilero` | `AddOpenConnectServerVadSilero` | `Microsoft.Extensions.DependencyInjection` | `OpenConnect.Core` | 1.0.0-preview | `dotnet build -c Release -warnaserror` (PASS) | implement (isolated per-capture state, 24k->16k resampling, sequence gap reset) |
| `AddOpenConnectCloudTranscription` | `AddOpenConnectCloudTranscription` | `Microsoft.Extensions.DependencyInjection` | `OpenConnect.Core` | 1.0.0-preview | `dotnet build -c Release -warnaserror` (PASS) | implement (evaluates strict-local audio denial, provider verification) |
| `AddOpenConnectNavigator` | `AddOpenConnectNavigator` | `Microsoft.Extensions.DependencyInjection` | `OpenConnect.Core` | 1.0.0-preview | `dotnet build -c Release -warnaserror` (PASS) | implement |
| `AddQdrantVactor` | `AddOpenConnectQdrantVactor` | `Microsoft.Extensions.DependencyInjection` | `OpenConnect.QdrantVactor` / `OpenConnect.Core` | 1.0.0-preview | `dotnet build -c Release -warnaserror` (PASS) | reuse / alias |
| `AddOpenConnectSecurityWorker` | `AddOpenConnectSecurityWorker` | `Microsoft.Extensions.DependencyInjection` | `OpenConnect.Core` | 1.0.0-preview | `dotnet build -c Release -warnaserror` (PASS) | implement (canonical registry wrapper, `vuminkosimatibe/openconnect-worker-security`) |
| `AddOpenConnectClassificationWorker` | `AddOpenConnectClassificationWorker` | `Microsoft.Extensions.DependencyInjection` | `OpenConnect.Core` | 1.0.0-preview | `dotnet build -c Release -warnaserror` (PASS) | implement (canonical registry wrapper, `vuminkosimatibe/openconnect-worker-classification`) |
| `AddOpenConnectTranslationWorker` | `AddOpenConnectTranslationWorker` | `Microsoft.Extensions.DependencyInjection` | `OpenConnect.Core` | 1.0.0-preview | `dotnet build -c Release -warnaserror` (PASS) | implement (canonical registry wrapper, `vuminkosimatibe/openconnect-worker-nllb`) |
| `AddOpenConnectInferenceWorker` | `AddOpenConnectInferenceWorker` | `Microsoft.Extensions.DependencyInjection` | `OpenConnect.Core` | 1.0.0-preview | `dotnet build -c Release -warnaserror` (PASS) | implement (canonical registry wrapper, `vuminkosimatibe/openconnect-worker-inference`) |
| `AddOpenConnectTranscriptionWorker` | `AddOpenConnectTranscriptionWorker` | `Microsoft.Extensions.DependencyInjection` | `OpenConnect.Core` | 1.0.0-preview | `dotnet build -c Release -warnaserror` (PASS) | implement (canonical registry wrapper, `vuminkosimatibe/openconnect-worker-transcription`) |
| `AddOpenConnectRealtimeVoiceWorker` | `AddOpenConnectRealtimeVoiceWorker` | `Microsoft.Extensions.DependencyInjection` | `OpenConnect.Core` | 1.0.0-preview | `dotnet build -c Release -warnaserror` (PASS) | implement (canonical registry wrapper, `vuminkosimatibe/openconnect-worker-realtime-voice`) |
| `AddOpenConnectKokoroWorker` | `AddOpenConnectKokoroWorker` | `Microsoft.Extensions.DependencyInjection` | `OpenConnect.Core` | 1.0.0-preview | `dotnet build -c Release -warnaserror` (PASS) | implement (canonical registry wrapper, `vuminkosimatibe/openconnect-worker-kokoro`) |
| `AddOpenConnectMmsTtsWorker` | `AddOpenConnectMmsTtsWorker` | `Microsoft.Extensions.DependencyInjection` | `OpenConnect.Core` | 1.0.0-preview | `dotnet build -c Release -warnaserror` (PASS) | implement (canonical registry wrapper, `vuminkosimatibe/openconnect-worker-mms-tts`) |
| `AddOpenConnectPlaywrightWorker` | `AddOpenConnectPlaywrightWorker` | `Microsoft.Extensions.DependencyInjection` | `OpenConnect.Core` | 1.0.0-preview | `dotnet build -c Release -warnaserror` (PASS) | implement (canonical registry wrapper, `vuminkosimatibe/openconnect-worker-navigator-playwright`) |
| `AddOpenConnectOperationalAgents` | `AddOpenConnectOperationalAgents` | `Microsoft.Extensions.DependencyInjection` | `OpenConnect.Core` | 1.0.0-preview | `dotnet build -c Release -warnaserror` (PASS) | implement (exclusivity guard, `vuminkosimatibe/openconnect-worker-operational-agents`) |
| `AddOpenConnectOutbox` | `AddOpenConnectOutbox` | `Microsoft.Extensions.DependencyInjection` | `OpenConnect.Core` | 1.0.0-preview | `dotnet build -c Release -warnaserror` (PASS) | implement (exclusivity guard, `vuminkosimatibe/openconnect-worker-outbox`) |
| `MapOpenConnect` | `MapOpenConnect` | `OpenConnect.Hosting.AspNetCore` | `OpenConnect.Hosting.AspNetCore` | 1.0.0-preview | `dotnet build -c Release -warnaserror` (PASS) | reuse (idempotent, calls sub-mappings safely) |
| `MapOpenConnectVoice` | `MapOpenConnectVoice` | `OpenConnect.Hosting.AspNetCore` | `OpenConnect.Hosting.AspNetCore` | 1.0.0-preview | `dotnet build -c Release -warnaserror` (PASS) | implement (idempotent route mapping, bounded audio upload gate) |
| `MapOpenConnectNavigator` | `MapOpenConnectNavigator` | `OpenConnect.Hosting.AspNetCore` | `OpenConnect.Hosting.AspNetCore` | 1.0.0-preview | `dotnet build -c Release -warnaserror` (PASS) | implement (idempotent route mapping, navigator worker status) |
| `MapOpenConnectMcp` | `MapOpenConnectMcp` | `OpenConnect.Hosting.AspNetCore` | `OpenConnect.Hosting.AspNetCore` | 1.0.0-preview | `dotnet build -c Release -warnaserror` (PASS) | implement (idempotent route mapping, tool catalog lookup) |

---

## 2. Key Architecture Decisions & Invariants

1. **One Canonical Worker Capability Registry**:
   All 11 worker convenience extensions (`AddOpenConnect*Worker`, `AddOpenConnectOperationalAgents`, `AddOpenConnectOutbox`) register into a single `IWorkerCapabilityRegistry` (and synchronize with `ICapabilityRegistry`). They do not spawn separate runtime registries or competing discovery systems.

2. **Embedded vs Standalone Hosting Exclusivity**:
   `OperationalAgents` and `Outbox` enforce strict exclusivity. Registering conflicting modes (e.g., both embedded and standalone) or contradictory flags throws `InvalidOperationException` with error code `ErrorCodes.WorkerHostingConflict` (`worker_hosting_conflict`).

3. **Single Host Privacy Firewall**:
   `AddOpenConnectPrivacy` configures the host-level `IPrivacyFirewall`, `ITokenVault`, `PolicyEngine`, and `IEgressGate` using `EnsureHostPrivacyServices` without creating duplicate instances across multiple configuration invocations.

4. **Isolated Server Silero VAD**:
   `ServerSileroVadEngine` guarantees complete isolation across capture streams, automatic 24kHz to 16kHz resampling, sequence gap and sample rate change state resets, and explicit per-capture cleanup.

5. **Cloud Route Policy**:
   `CloudTranscriptionPolicyEvaluator` strictly enforces `ErrorCodes.VoiceEgressDenied` (`voice_egress_denied`) under `StrictLocal` or `LocalOnly` privacy modes when explicit audio egress approval is absent.

6. **Endpoint Route Idempotency**:
   `MapOpenConnect()`, `MapOpenConnectVoice()`, `MapOpenConnectNavigator()`, and `MapOpenConnectMcp()` track mapped groups on the `IEndpointRouteBuilder` using `ConditionalWeakTable`, ensuring they can be called in any order or repeatedly without route collision or `AmbiguousMatchException`.

7. **Persistence Invariant**:
   `OpenConnect.Persistence.EFCore` is the single relational persistence adapter. Application contexts own their migrations (`dotnet ef migrations add`). Startup auto-DDL is strictly prohibited. `EfContextRegistrationMarker` ensures multi-container isolation in unit tests.
