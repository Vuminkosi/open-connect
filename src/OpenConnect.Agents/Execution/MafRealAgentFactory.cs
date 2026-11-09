using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenConnect.Abstractions;

namespace OpenConnect.Agents.Execution;

/// <summary>Runs real Microsoft Agent Framework agents and records their execution trace.</summary>
public sealed class MafRealAgentFactory
{
    private readonly IAgentDefinitionRegistry _registry;
    private readonly IChatClient _chatClient;
    private readonly object _sync = new();
    private readonly List<ExecutionTraceRecord> _traces = [];

    /// <summary>Creates the factory with registered agent definitions and a chat client.</summary>
    public MafRealAgentFactory(IAgentDefinitionRegistry registry, IChatClient chatClient)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
    }

    /// <summary>Creates an MAF agent with host-authorized tools and resolved instructions.</summary>
    /// <remarks>When instructions are not supplied, the agent name remains the legacy instruction default. Tool authorization and instruction loading belong to the host.</remarks>
    public AIAgent CreateAgent(AgentId agentId, IEnumerable<AITool>? tools = null, string? instructions = null)
    {
        var definition = _registry.Get(agentId) ?? throw new InvalidOperationException("Agent definition is not registered.");
        var resolvedInstructions = string.IsNullOrWhiteSpace(instructions) ? definition.Name : instructions;
        return _chatClient.AsAIAgent(name: definition.Name, instructions: resolvedInstructions, tools: tools?.ToList());
    }

    /// <summary>Runs one turn on a real MAF agent and records its root/agent trace.</summary>
    public async Task<AgentResponse> RunAsync(AgentId agentId, string prompt, CancellationToken cancellationToken = default)
        => await RunAsync(agentId, prompt, tools: null, cancellationToken, instructions: null).ConfigureAwait(false);

    /// <summary>Runs one turn using host-authorized tools. Tool discovery and approval belong to OpenConnect.Tools; this factory performs no authorization.</summary>
    public async Task<AgentResponse> RunAsync(AgentId agentId, string prompt, IEnumerable<AITool>? tools, CancellationToken cancellationToken = default, string? instructions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        var definition = _registry.Get(agentId) ?? throw new InvalidOperationException("Agent definition is not registered.");
        var parent = new ExecutionTraceRecord(Guid.NewGuid(), null, "execution", "lead", DateTimeOffset.UtcNow, null);
        var child = new ExecutionTraceRecord(Guid.NewGuid(), parent.Id, definition.Id.Value, "agent", DateTimeOffset.UtcNow, null);
        AddTrace(parent);
        AddTrace(child);

        try
        {
            return await CreateAgent(agentId, tools, instructions).RunAsync(prompt, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CompleteTrace(child.Id);
            CompleteTrace(parent.Id);
        }
    }

    /// <summary>Returns a snapshot of all execution records.</summary>
    public IReadOnlyList<ExecutionTraceRecord> GetTrace()
    {
        lock (_sync) return _traces.ToArray();
    }

    private void AddTrace(ExecutionTraceRecord trace)
    {
        lock (_sync) _traces.Add(trace);
    }

    private void CompleteTrace(Guid id)
    {
        lock (_sync)
        {
            var index = _traces.FindIndex(trace => trace.Id == id);
            _traces[index] = _traces[index] with { CompletedAt = DateTimeOffset.UtcNow };
        }
    }
}
