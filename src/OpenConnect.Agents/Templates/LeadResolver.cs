using System.Security.Cryptography;
using System.Text;
using OpenConnect.Abstractions;

namespace OpenConnect.Agents.Templates;

/// <summary>Resolves the uniquely registered lead against a per-request catalog snapshot.</summary>
public sealed class LeadResolver
{
    /// <summary>Lead resolution bound to the task request that produced it.</summary>
    /// <param name="Lead">The registered lead definition.</param>
    /// <param name="CatalogRevision">Deterministic catalog-only revision.</param>
    /// <param name="BoundTaskId">Task identifier supplied by the request.</param>
    public sealed record LeadResolution(AgentDefinition Lead, string CatalogRevision, TaskId BoundTaskId);

    /// <summary>Resolves the registered lead and captures the catalog revision for this request.</summary>
    /// <param name="taskId">Request task identifier, used to bind the result to the request.</param>
    /// <param name="catalog">Current catalog definitions.</param>
    /// <param name="registration">The single lead registration.</param>
    /// <returns>The lead definition and deterministic catalog revision snapshot.</returns>
    /// <exception cref="InvalidOperationException">No lead is registered, the lead is absent, or the catalog differs from its registration snapshot.</exception>
    public (AgentDefinition Lead, string CatalogRevision) Resolve(TaskId taskId,
        IReadOnlyList<AgentDefinition> catalog, AgentRegistrationExtensions.LeadAgentRegistration registration)
    {
        var resolution = ResolveBound(taskId, catalog, registration);
        return (resolution.Lead, resolution.CatalogRevision);
    }

    /// <summary>Resolves a lead and retains the request task binding without including it in the catalog revision.</summary>
    /// <param name="taskId">Request task identifier.</param>
    /// <param name="catalog">Current catalog definitions.</param>
    /// <param name="registration">The single lead registration.</param>
    /// <returns>The lead, catalog snapshot revision, and bound task identifier.</returns>
    /// <exception cref="ArgumentException">The task identifier is default.</exception>
    /// <exception cref="InvalidOperationException">Lead resolution fails or the catalog differs from its registration snapshot.</exception>
    public LeadResolution ResolveBound(TaskId taskId,
        IReadOnlyList<AgentDefinition> catalog, AgentRegistrationExtensions.LeadAgentRegistration registration)
    {
        if (taskId.Value == default) throw new ArgumentException("A task identifier is required.", nameof(taskId));
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(registration);
        if (registration.AgentId.Value is null)
            throw new InvalidOperationException("No lead agent is registered.");

        var matches = catalog.Where(definition => definition.Id == registration.AgentId).ToArray();
        if (matches.Length == 0)
            throw new InvalidOperationException("The registered lead agent is not present in the catalog.");
        if (matches.Length != 1)
            throw new InvalidOperationException("The catalog contains multiple definitions for the lead agent.");

        var revision = ComputeRevision(catalog);
        if (registration.CatalogRevision is not null && !StringComparer.Ordinal.Equals(registration.CatalogRevision, revision))
            throw new InvalidOperationException("The agent catalog changed after lead registration.");
        return new LeadResolution(matches[0], revision, taskId);
    }

    private static string ComputeRevision(IReadOnlyList<AgentDefinition> definitions)
    {
        var canonical = string.Join("\n", definitions.OrderBy(item => item.Id.Value.ToString(), StringComparer.Ordinal)
            .Select(item => $"{item.Id.Value}:{item.Version}:{item.PolicyRevision}:{item.InstructionsRevision}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
