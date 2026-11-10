# OpenConnect.Agents

MAF 1.27.0 foundation. `MafRealAgentFactory` builds real agents via
`IChatClient.AsAIAgent(...)` and runs one turn via `RunAsync(...)`;
session state uses the native `AgentSession` codec, and Workflow runs
use the native Workflow primitive via `InProcessExecution` with
host authorization enforced before any model call.

Supported: Direct execution and host-constructed Workflow
(Sequential / Parallel / Handoff / Supervisor delegation runners).
Unsupported per Rule 2 (explicit gates, never fabricated):
Harness, Durable execution, MCP integration, A2A integration.

Unsupported profiles fail closed: `OrchestrationProfileRouter`
(`MafRuntimeProbe` capability matrix) throws
`MafCapabilityUnavailableException` before any execution is attempted.

Policy layers are not execution rebuilds: `AgentCatalog`,
`DelegationGate`, `RunBudgetLedger`, and `ContextProjection`
(authorization, budget, scope) sit above the MAF runtime.

Execution traces record parent/child records around the real MAF call.
