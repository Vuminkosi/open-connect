using OpenConnect.Abstractions;

namespace OpenConnect.Agents.Execution;

/// <summary>Creates agent execution scopes and records parent-child delegation.</summary>
public sealed class MafAgentFactory
{
    private readonly IAgentDefinitionRegistry _registry;
    private readonly object _sync = new();
    private readonly List<ExecutionTraceRecord> _traces = [];

    /// <summary>Creates the factory with the application's registered agent definitions.</summary>
    public MafAgentFactory(IAgentDefinitionRegistry registry) =>
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    /// <summary>Starts a root agent run when its definition is registered.</summary>
    public AgentRun CreateLeadAgent(AgentId agentId) => Create(agentId, null, "lead");

    /// <summary>Starts a delegated run under the supplied parent execution.</summary>
    public AgentRun Delegate(AgentRun parent, AgentId agentId)
    {
        ArgumentNullException.ThrowIfNull(parent);
        return Create(agentId, parent.Id, "delegation");
    }

    /// <summary>Returns a stable snapshot of recorded execution traces.</summary>
    public IReadOnlyList<ExecutionTraceRecord> GetTrace()
    {
        lock (_sync) return _traces.ToArray();
    }

    private AgentRun Create(AgentId agentId, Guid? parentId, string kind)
    {
        var definition = _registry.Get(agentId) ?? throw new InvalidOperationException("Agent definition is not registered.");
        var trace = new ExecutionTraceRecord(Guid.NewGuid(), parentId, definition.Id.Value, kind, DateTimeOffset.UtcNow, null);
        lock (_sync) _traces.Add(trace);
        return new AgentRun(trace.Id, definition);
    }
}

/// <summary>A local execution handle for an agent definition.</summary>
public sealed record AgentRun(Guid Id, AgentDefinition Definition);
