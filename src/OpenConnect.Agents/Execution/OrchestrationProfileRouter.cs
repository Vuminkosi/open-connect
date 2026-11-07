using OpenConnect.Abstractions;
using OpenConnect.Agents.Catalog;

namespace OpenConnect.Agents.Execution;

/// <summary>Identifies the host runner selected for an orchestration profile.</summary>
public enum OrchestrationRunnerKey
{
    /// <summary>Runs one agent without delegation.</summary>
    DirectRunner,
    /// <summary>Runs authorized children in sequence.</summary>
    SequentialRunner,
    /// <summary>Runs authorized children concurrently.</summary>
    ParallelRunner,
    /// <summary>Transfers a supplied projection to an authorized child.</summary>
    HandoffRunner,
    /// <summary>Runs authorized supervisor delegation.</summary>
    SupervisorRunner
}

/// <summary>Maps declared orchestration profiles to supported framework runners and validates profiles.</summary>
public static class OrchestrationProfileRouter
{
    /// <summary>Selects the one runner corresponding to a profile, failing when its MAF primitive is unavailable.</summary>
    /// <param name="mode">Configured orchestration mode.</param>
    /// <param name="mafDirect">Whether direct execution is supported.</param>
    /// <param name="mafSequential">Whether sequential orchestration is supported.</param>
    /// <param name="mafParallel">Whether parallel orchestration is supported.</param>
    /// <param name="mafHandoff">Whether handoff orchestration is supported.</param>
    /// <param name="mafSupervisor">Whether supervisor delegation is supported.</param>
    /// <returns>The sole runner key for the configured mode.</returns>
    public static OrchestrationRunnerKey Resolve(AgentOrchestrationMode mode, bool mafDirect, bool mafSequential,
        bool mafParallel, bool mafHandoff, bool mafSupervisor)
    {
        var (supported, runner, primitive) = mode switch
        {
            AgentOrchestrationMode.Direct => (mafDirect, OrchestrationRunnerKey.DirectRunner, "Direct"),
            AgentOrchestrationMode.Sequential => (mafSequential, OrchestrationRunnerKey.SequentialRunner, "Sequential"),
            AgentOrchestrationMode.Parallel => (mafParallel, OrchestrationRunnerKey.ParallelRunner, "Parallel"),
            AgentOrchestrationMode.Handoff => (mafHandoff, OrchestrationRunnerKey.HandoffRunner, "Handoff"),
            AgentOrchestrationMode.SupervisorDelegation => (mafSupervisor, OrchestrationRunnerKey.SupervisorRunner, "SupervisorDelegation"),
            _ => throw new InvalidOperationException($"Undefined orchestration mode '{mode}'.")
        };

        if (!supported)
            throw new InvalidOperationException($"Orchestration mode '{mode}' requires unsupported MAF primitive '{primitive}'.");
        return runner;
    }

    /// <summary>Validates that a definition selects only a defined, framework-supported profile.</summary>
    /// <param name="definition">Agent definition to validate.</param>
    /// <param name="mafDirect">Whether direct execution is supported.</param>
    /// <param name="mafSequential">Whether sequential orchestration is supported.</param>
    /// <param name="mafParallel">Whether parallel orchestration is supported.</param>
    /// <param name="mafHandoff">Whether handoff orchestration is supported.</param>
    /// <param name="mafSupervisor">Whether supervisor delegation is supported.</param>
    public static void ValidateAgentProfile(AgentDefinition definition, bool mafDirect, bool mafSequential,
        bool mafParallel, bool mafHandoff, bool mafSupervisor)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Resolve(definition.Orchestration.Mode, mafDirect, mafSequential, mafParallel, mafHandoff, mafSupervisor);
        if (definition.Orchestration.MaxDepth < 0 || definition.Orchestration.MaxDepth > AgentCatalog.FrameworkMaxDepth)
            throw new InvalidOperationException($"Maximum delegation depth must be between 0 and {AgentCatalog.FrameworkMaxDepth}.");
        if (!Enum.IsDefined(definition.Orchestration.ResultMergeMode))
            throw new AgentCatalogException($"Undefined result merge mode '{definition.Orchestration.ResultMergeMode}'.");
    }
}
