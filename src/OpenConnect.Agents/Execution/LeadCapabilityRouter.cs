using OpenConnect.Abstractions;

namespace OpenConnect.Agents.Execution;

/// <summary>
/// Routes declared capabilities to children explicitly permitted by a lead agent's registered delegation policy.
/// This is a routing map, not runtime authorization: the DelegationGate (or host gate) must still grant authority
/// before any selected child is run.
/// </summary>
public sealed class LeadCapabilityRouter
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<AgentId>> _routes;

    /// <summary>Creates an immutable capability-to-child routing map.</summary>
    /// <param name="routes">Capability keys and ordered candidate child identifiers.</param>
    /// <exception cref="ArgumentNullException">The routes dictionary is null.</exception>
    /// <exception cref="ArgumentException">A capability key is empty or a route has no children.</exception>
    public LeadCapabilityRouter(IReadOnlyDictionary<string, IReadOnlyList<AgentId>> routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var copy = new Dictionary<string, IReadOnlyList<AgentId>>(StringComparer.Ordinal);
        foreach (var (capability, children) in routes)
        {
            if (string.IsNullOrWhiteSpace(capability))
                throw new ArgumentException("Capability keys must not be empty or whitespace.", nameof(routes));
            if (children is null || children.Count == 0)
                throw new ArgumentException($"Capability '{capability}' must map to at least one child.", nameof(routes));
            if (!copy.TryAdd(capability, Array.AsReadOnly(children.ToArray())))
                throw new ArgumentException("Capability keys must be unique using ordinal comparison.", nameof(routes));
        }
        _routes = new System.Collections.ObjectModel.ReadOnlyDictionary<string, IReadOnlyList<AgentId>>(copy);
    }

    /// <summary>Validates that the lead and every mapped child belong to the registered allowed delegation graph.</summary>
    /// <param name="catalog">Registered agent definitions.</param>
    /// <param name="leadId">Lead agent whose orchestration policy constrains the routes.</param>
    /// <exception cref="ArgumentNullException">The catalog is null.</exception>
    /// <exception cref="InvalidOperationException">The lead or a child is not registered, or a child is not allowed.</exception>
    public void Validate(IAgentDefinitionRegistry catalog, AgentId leadId)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var lead = catalog.Get(leadId)
            ?? throw new InvalidOperationException("The lead agent is not registered.");
        foreach (var (capability, children) in _routes)
        {
            foreach (var childId in children)
            {
                if (catalog.Get(childId) is null)
                    throw new InvalidOperationException($"Capability '{capability}' maps to unregistered child '{childId}'.");
                if (lead.Orchestration.AllowedChildAgents?.Contains(childId) != true)
                    throw new InvalidOperationException($"Capability '{capability}' maps to child '{childId}' not allowed by the lead.");
            }
        }
    }

    /// <summary>Returns the first registered route target for a capability.</summary>
    /// <param name="capability">Capability to route.</param>
    /// <returns>The first-listed child; list order defines deterministic selection.</returns>
    /// <exception cref="UnauthorizedAccessException">The capability is blank or has no route.</exception>
    public AgentId ResolveChild(string capability)
    {
        if (!string.IsNullOrWhiteSpace(capability) && _routes.TryGetValue(capability, out var children))
            return children[0];
        throw new UnauthorizedAccessException("No child agent is available for this capability.");
    }
}
