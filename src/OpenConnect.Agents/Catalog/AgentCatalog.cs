using OpenConnect.Abstractions;

namespace OpenConnect.Agents.Catalog;

/// <summary>Thread-safe catalog that validates the complete registered delegation graph.</summary>
public sealed class AgentCatalog : IAgentDefinitionRegistry
{
    /// <summary>Maximum delegation depth supported by the framework.</summary>
    public const int FrameworkMaxDepth = 2;
    private readonly object _sync = new();
    private Dictionary<AgentId, AgentDefinition> _definitions = [];

    /// <summary>Validates and atomically registers an agent definition.</summary>
    /// <param name="definition">The definition to register.</param>
    public void Register(AgentDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ValidateDefinition(definition);
        lock (_sync)
        {
            if (_definitions.ContainsKey(definition.Id))
                throw new AgentCatalogException($"Agent '{definition.Id}' is already registered.");
            var candidate = new Dictionary<AgentId, AgentDefinition>(_definitions) { [definition.Id] = definition };
            ValidateGraph(candidate);
            _definitions = candidate;
        }
    }

    /// <summary>Validates a proposed catalog snapshot, including unresolved child references.</summary>
    /// <param name="definitions">The definitions to validate.</param>
    public static void Validate(IEnumerable<AgentDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var map = new Dictionary<AgentId, AgentDefinition>();
        foreach (var definition in definitions)
        {
            ArgumentNullException.ThrowIfNull(definition);
            ValidateDefinition(definition);
            if (!map.TryAdd(definition.Id, definition))
                throw new AgentCatalogException($"Agent '{definition.Id}' is duplicated.");
        }
        ValidateGraph(map);
    }

    /// <inheritdoc />
    public AgentDefinition? Get(AgentId agentId)
    {
        lock (_sync) return _definitions.GetValueOrDefault(agentId);
    }

    /// <inheritdoc />
    public IReadOnlyList<AgentDefinition> GetAll()
    {
        lock (_sync) return _definitions.Values.OrderBy(x => x.Id.Value, StringComparer.Ordinal).ToArray();
    }

    private static void ValidateDefinition(AgentDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Id.Value) || string.IsNullOrWhiteSpace(definition.Name))
            throw new AgentCatalogException("Agent id and name are required.");
        if (string.IsNullOrWhiteSpace(definition.Version) || string.IsNullOrWhiteSpace(definition.PolicyRevision))
            throw new AgentCatalogException("Agent and policy revisions are required.");
        var policy = definition.Orchestration ?? throw new AgentCatalogException("Orchestration policy is required.");
        if (!Enum.IsDefined(policy.Mode) || !Enum.IsDefined(policy.ResultMergeMode))
            throw new AgentCatalogException("Agent orchestration policy contains an unsupported mode.");
        if (policy.MaxDepth < 0 || policy.MaxDepth > FrameworkMaxDepth)
            throw new AgentCatalogException($"Maximum delegation depth must be between 0 and {FrameworkMaxDepth}.");
        if (policy.MaxChildren < 0) throw new AgentCatalogException("Maximum child count cannot be negative.");
        var budget = definition.Budget ?? throw new AgentCatalogException("Agent budget is required.");
        if (budget.MaxToolCalls < 0 || budget.MaxModelCalls < 0 || budget.MaxTokens < 0 ||
            budget.MaxConcurrentAgents < 0 || budget.MaxQueuedAgents < 0 || budget.MaxTotalAgents < 0 ||
            budget.MaxRuntime < TimeSpan.Zero)
            throw new AgentCatalogException("Agent budget limits cannot be negative.");
        if (definition.ContextPolicy.MaxInputTokens < 0 || definition.ContextPolicy.MaxOutputTokens < 0)
            throw new AgentCatalogException("Context token limits cannot be negative.");
        if (definition.Orchestration.AllowedChildAgents?.Contains(definition.Id) == true)
            throw new AgentCatalogException("An agent cannot delegate to itself.");
    }

    private static void ValidateGraph(IReadOnlyDictionary<AgentId, AgentDefinition> definitions)
    {
        foreach (var definition in definitions.Values)
            foreach (var child in definition.Orchestration.AllowedChildAgents ?? [])
                if (!definitions.ContainsKey(child))
                    throw new AgentCatalogException($"Agent '{definition.Id}' references unknown child '{child}'.");

        var active = new HashSet<AgentId>();
        foreach (var id in definitions.Keys) Visit(id, 0);

        void Visit(AgentId id, int depth)
        {
            if (depth > FrameworkMaxDepth)
                throw new DelegationDepthExceededException(depth, FrameworkMaxDepth);
            if (active.Contains(id)) throw new AgentCatalogException($"Delegation graph contains a cycle at '{id}'.");
            active.Add(id);
            foreach (var child in definitions[id].Orchestration.AllowedChildAgents ?? []) Visit(child, depth + 1);
            active.Remove(id);
        }
    }
}
