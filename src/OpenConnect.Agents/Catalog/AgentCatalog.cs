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
        ValidateOperationalPolicy(definition);
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

    private static void ValidateOperationalPolicy(AgentDefinition definition)
    {
        if (!Enum.IsDefined(definition.ExecutionMode) || !Enum.IsDefined(definition.IdentityMode))
            throw new AgentCatalogException("Agent execution and identity modes must be supported.");

        if (definition.ExecutionMode == AgentExecutionMode.Operational)
        {
            var operational = definition.Operational ?? throw new AgentCatalogException("Operational agents require an operational policy.");
            if (definition.IdentityMode != AgentIdentityMode.WorkloadIdentity || string.IsNullOrWhiteSpace(operational.WorkloadIdentityRef))
                throw new AgentCatalogException("Operational agents require a configured workload identity and cannot use active-user identity.");
            if (!Enum.IsDefined(operational.Activation) || !Enum.IsDefined(operational.Misfire) || !Enum.IsDefined(operational.Concurrency))
                throw new AgentCatalogException("Operational policy contains an unsupported activation, misfire, or concurrency mode.");
            if (operational.MaxParallelRuns < 1 || operational.MaxRunDuration <= TimeSpan.Zero)
                throw new AgentCatalogException("Operational run limits must allow at least one parallel run and a positive duration.");
            if (operational.Activation == AgentActivationMode.Scheduled)
            {
                if (string.IsNullOrWhiteSpace(operational.Cron))
                    throw new AgentCatalogException("Scheduled operational agents require a cron schedule.");
                if (!IsCronShapeValid(operational.Cron))
                    throw new AgentCatalogException("Scheduled operational agents require a valid five- or six-field cron schedule.");
                if (string.IsNullOrWhiteSpace(operational.TimeZoneId))
                    throw new AgentCatalogException("Scheduled operational agents require a time zone.");
                try { _ = TimeZoneInfo.FindSystemTimeZoneById(operational.TimeZoneId); }
                catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
                {
                    throw new AgentCatalogException($"Scheduled operational agent time zone '{operational.TimeZoneId}' is not recognized or is invalid.", ex);
                }
            }
            else if (!string.IsNullOrWhiteSpace(operational.Cron))
                throw new AgentCatalogException("Cron schedules are valid only for scheduled activation.");
        }
        else
        {
            if (definition.IdentityMode != AgentIdentityMode.ActiveUser)
                throw new AgentCatalogException("Interactive agents must use active-user identity.");
            if (definition.Operational is { Activation: not AgentActivationMode.OnDemand })
                throw new AgentCatalogException("Interactive agents support on-demand activation only.");
            if (definition.Operational is { Cron: not null })
                throw new AgentCatalogException("Cron schedules are valid only for scheduled operational agents.");
        }
    }

    private static bool IsCronShapeValid(string cron)
    {
        var fields = cron.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length is not (5 or 6)) return false;
        var bounds = fields.Length == 5
            ? new[] { (0, 59), (0, 23), (1, 31), (1, 12), (0, 7) }
            : new[] { (0, 59), (0, 59), (0, 23), (1, 31), (1, 12), (0, 7) };
        for (var i = 0; i < fields.Length; i++)
            if (!IsCronFieldValid(fields[i], bounds[i].Item1, bounds[i].Item2)) return false;
        return true;
    }

    private static bool IsCronFieldValid(string field, int minimum, int maximum)
    {
        foreach (var item in field.Split(','))
        {
            var parts = item.Split('/');
            if (parts.Length > 2 || (parts.Length == 2 && (!int.TryParse(parts[1], out var step) || step < 1))) return false;
            var range = parts[0];
            if (range == "*") continue;
            var endpoints = range.Split('-');
            if (endpoints.Length is < 1 or > 2 || !int.TryParse(endpoints[0], out var start) || start < minimum || start > maximum) return false;
            if (endpoints.Length == 2 && (!int.TryParse(endpoints[1], out var end) || end < start || end > maximum)) return false;
        }
        return true;
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
