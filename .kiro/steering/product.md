---
inclusion: auto
name: openconnect-product
description: OpenConnect product mission, scope, and roadmap for Kiroween 2026 Skeleton Crew
---

# OpenConnect Product Steering

## Mission

OpenConnect is an **open-source, privacy-first, .NET-first, configuration-driven AI application framework** layered on Microsoft Agent Framework (MAF). It provides reusable infrastructure for secure, multimodal, multi-turn, and multi-agent AI applications, allowing developers to focus on their products instead of rebuilding foundational AI infrastructure.

**Core principle:** Build on Microsoft Agent Framework, not around it.

## Target Developers

- .NET/Blazor developers building AI-powered applications
- Teams requiring privacy-preserving AI interactions
- Organizations needing multi-tenant, secure AI infrastructure
- Developers wanting model-agnostic LLM integration

## Supported Use Cases

### Primary Use Cases

1. **Streaming multi-turn conversations** with persistent sessions
2. **Multi-agent delegation** with observable execution trees
3. **Privacy-preserving document processing** with PII masking and restoration
4. **Voice-enabled applications** with STT/TTS, VAD, and live turn coordination
5. **Tool-augmented agents** with authorization and approval workflows
6. **Live and recorded speech** processing with interruption support

### Kiroween 2026 Skeleton Crew Applications

OpenConnect must support two demonstration applications using shared packages:

#### VoiceDesk
Demonstrates:
- Streaming multi-turn conversations
- Live and recorded speech
- Voice Activity Detection (VAD)
- Text-to-Speech (TTS)
- Persistent sessions
- Agent delegation

#### PrivateDocs
Demonstrates:
- Privacy-preserving document processing
- PII masking
- Model inference with privacy boundaries
- Authorized restoration
- Tool security
- Observable agent execution

**Constraint:** Both applications must use shared OpenConnect packages. No duplicated gateway, session, privacy, or agent infrastructure.

## Product Boundaries

### In Scope

1. `services.AddOpenConnect()` registers default lead agent and enabled privacy firewall
2. Declarative runtime instructions and agent/subagent registry
3. Agent-scoped tool discovery, schema validation, deterministic authorization
4. MAF-native agent instances/workflows/tool execution
5. HTTP task API and WebSocket event transport
6. Durable conversation state with PostgreSQL persistence
7. NATS JetStream event backbone for worker dispatch
8. Separately deployed Python worker containers:
   - Security (privacy detection) - required for strict-mode egress
   - Classification, MMS-TTS, NLLB translation
   - Self-hosted inference, transcription, realtime-voice
9. `AddOpenConnectClient()` .NET client
10. `<OpenConnectProvider />` and `<MiniComposer />` MudBlazor components
11. `@openconnect/client` TypeScript SDK (no React dependency initially)
12. Documentation website with AI assistant powered by OpenConnect

### Explicitly Out of Scope for Core v1

- Generic commercial AI application (framework, not product)
- Domain-specific workflows (health/cases)
- Mandatory model downloads
- Browser-side direct NATS connections
- Implicit agent self-granted authority
- Automatic remote PII detection in strict-local mode
- Mandatory React components
- Automatic GPU provisioning
- Promise of "no code" for custom business tools
- Distribution of unlicensed model weights

## Key Design Decisions

### Security and Privacy by Default

- Privacy firewall **enabled by default**
- Authentication, authorization, and tenant isolation **always active**
- Security authorization remains active even if privacy masking is explicitly disabled
- Strict-local mode requires healthy local detector before external egress

### Model Independence Without False Equivalence

- Router does not assume every LLM supports tools, streaming, structured outputs, or vision
- Capability descriptors advertise actual supported features
- No implicit fallback between providers when it changes privacy, language, cost, or behavior

### Composable Libraries

- Core abstractions have no ASP.NET, Blazor, NATS, PostgreSQL, Python, or cloud-provider dependency
- Packages are independent modules with clear dependency graphs
- Workers are optional, separately deployed containers

## Roadmap and Kiroween Objectives

### Phase 1: Foundation (Current)
- Repository setup, licensing, source attribution
- Core abstractions and DI registration
- MAF integration and agent runtime
- Privacy firewall with security worker
- Durable sessions and NATS transport

### Phase 2: Core Capabilities
- Tool broker with authorization
- HTTP/WebSocket gateway
- Blazor client components
- TypeScript SDK
- First worker integrations

### Phase 3: Voice and Multi-Modal
- Voice activity detection
- Transcription worker
- TTS integration
- Realtime dialogue worker

### Phase 4: Production Readiness
- Documentation website
- AI documentation assistant
- CI/CD and release automation
- Package publishing (NuGet, npm, GHCR)

## Success Criteria

1. **Reliability over feature count** - Working end-to-end vertical slices
2. **Real execution evidence** - Not architecture-only demonstrations
3. **Security and privacy by default** - All P0 security gates passed
4. **Build on MAF** - No parallel agent runtime implementation
5. **Model independence** - Truthful capability advertisement
