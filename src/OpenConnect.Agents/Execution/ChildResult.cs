using Microsoft.Agents.AI;
using OpenConnect.Abstractions;

namespace OpenConnect.Agents.Execution;

/// <summary>Result and lineage metadata from a child execution.</summary>
/// <param name="AgentId">Identifier of the child agent.</param>
/// <param name="ExecutionId">Identifier of the child execution.</param>
/// <param name="Response">Response returned by the child.</param>
/// <param name="Order">Original position of the child in the request.</param>
public sealed record ChildResult(AgentId AgentId, ExecutionId ExecutionId, AgentResponse Response, int Order);
