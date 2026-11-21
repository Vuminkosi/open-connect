---
title: Supported services and integrations
version: v1
summary: Select the libraries, infrastructure and optional workers needed for your application.
order: 15
---

# Supported services and integrations

OpenConnect is a set of host libraries and optional integrations. The table describes integration surfaces in this source tree, not a guarantee that a service is deployed, entitled, configured or ready in your environment. Check the release you install and the host's capability and readiness responses.

| Need | Integration | What the application must supply |
| --- | --- | --- |
| Tasks, sessions and client access | Core, ASP.NET Core gateway, .NET or TypeScript client | Authentication, tenant and principal identity, executor and model configuration |
| Agent execution | `OpenConnect.Agents` | An agent definition, supported execution profile, model provider and host policy; see [MAF integration](/docs/v1/maf-integration) |
| Privacy and egress | `OpenConnect.Privacy` | Policy, detector configuration and protected vault; see [privacy](/docs/v1/privacy) |
| Tools and MCP | `OpenConnect.Tools` and optional gateway MCP routes | Registrations, allowlists, authorization and approval policy; see [tools](/docs/v1/tools-and-mcp) |
| Durable application state | `OpenConnect.Persistence.EFCore` | Application-owned context, provider, reviewed migrations and database; see [persistence](/docs/v1/persistence) |
| Asynchronous transport | `OpenConnect.Transport.Nats` and outbox worker | NATS JetStream, streams, credentials and outbox deployment; see [workers](/docs/v1/nats-and-workers) |
| Blazor and browser composer | `OpenConnect.Components.Blazor` or `@openconnect/composer` | Host component providers, authenticated gateway and application UI integration; see [UI integration](/docs/v1/ui-integration) |
| Guided navigation and application interaction | Navigator and LiveUI packages | Host authorization, client connection, reviewed actions and optional inference; see [LiveUI](/docs/v1/liveui) |
| Document processing and knowledge | `OpenConnect.Documents`, `OpenConnect.Knowledge` and optional workers | Private worker deployment, host authorization, privacy gates, storage and model readiness; see [documents and knowledge](/docs/v1/documents-and-knowledge) |
| Agent semantic memory | `OpenConnect.QdrantVactor` | Scoped authorization, an embedding generator and a suitable store; see [semantic memory](/docs/v1/semantic-memory) |
| Speech and translation | Optional voice, transcription, synthesis and translation workers | Models, permissions and private transport; see [voice and translation](/docs/v1/voice-and-translation) |
| Telemetry and budgets | `OpenConnect.Observability`, `OpenConnect.Metering` | Safe exporters and an appropriate durable budget implementation; see [operations](/docs/v1/operations-and-metering) |
| Cloud workspace | Optional Cloud control plane and capability gateway | Identity, database, deployment, entitlements and credentials; see [Cloud workspace](/docs/v1/cloud-workspace) |

## Worker selection

The repository includes workers for security detection, classification, inference, translation, transcription, realtime voice, speech synthesis, browser navigation, operational tasks and outbox processing. Document intelligence, knowledge, vision and analytics workers have separate source projects. Consult the corresponding worker README and `samples/workers` in the source repository for its exact contract, prerequisites and sample commands. A worker's liveness does not establish model readiness; a ready advertisement does not grant access or bypass privacy checks. Models are not automatically downloaded by framework registration.

The .NET host remains the security boundary. Authorize each operation for the actual tenant and principal, evaluate the applicable privacy policy, and protect worker endpoints and credentials. Do not place raw prompts, audio, screenshots, secrets or token-vault mappings on NATS. Results and embeddings can also be sensitive and need access and retention controls.

## Choose by deployment

Start with core and the gateway only when the application needs HTTP access. Add a client in each consuming application. Add persistence and transport for the durability and delivery properties your deployment requires; neither follows automatically from adding the gateway. Deploy only the workers required by the chosen feature, verify their readiness, and test a real request through the host boundary. A source build or a catalog entry alone is not end-to-end evidence.

## Related links

- [Examples and sample projects](/docs/v1/examples)
- [Configuration reference](/docs/v1/configuration)
