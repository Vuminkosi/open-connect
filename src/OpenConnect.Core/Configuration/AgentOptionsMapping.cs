using OpenConnect.Abstractions;

namespace OpenConnect.Core.Configuration;

/// <summary>Maps validated agent registry settings to the immutable limits for one run.</summary>
/// <remarks>
/// Mapping table: <see cref="AgentBudget.MaxToolCalls"/> copies
/// <see cref="AgentRegistryOptions.MaxTotalToolCalls"/>; <see cref="AgentBudget.MaxModelCalls"/>
/// copies <see cref="AgentRegistryOptions.MaxModelCalls"/>; <see cref="AgentBudget.MaxTokens"/>
/// copies <see cref="AgentRegistryOptions.MaxTokens"/>; <see cref="AgentBudget.MaxRuntime"/>
/// copies <see cref="AgentRegistryOptions.MaxTurnDuration"/>; <see cref="AgentBudget.MaxConcurrentAgents"/>
/// copies <see cref="AgentRegistryOptions.MaxConcurrentSubagents"/>; <see cref="AgentBudget.MaxQueuedAgents"/>
/// copies <see cref="AgentRegistryOptions.MaxQueuedAgents"/>; <see cref="AgentBudget.MaxTotalAgents"/>
/// copies <see cref="AgentRegistryOptions.MaxTotalSubagentsPerRun"/>; and
/// <see cref="AgentBudget.MaxCostMicrocents"/> copies
/// <see cref="AgentRegistryOptions.MaxCostMicrocents"/>. Explicitly constructed
/// <see cref="AgentBudget"/> instances with zero limits retain their fail-closed meaning in the
/// ledger. Delegation depth remains enforced by the delegation policy layer and is not a
/// run-budget dimension. The unbounded sentinels (<see cref="long.MaxValue"/>/<see cref="int.MaxValue"/>)
/// mean unlimited for the token, queue, and cost dimensions.
/// </remarks>
public static class AgentOptionsMapping
{
    /// <summary>Creates per-run budget limits from agent registry options.</summary>
    /// <param name="options">The agent registry options to map.</param>
    /// <returns>An immutable budget containing the mapped limits.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public static AgentBudget ToRunBudget(AgentRegistryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new AgentBudget(
            MaxToolCalls: options.MaxTotalToolCalls,
            MaxModelCalls: options.MaxModelCalls,
            MaxTokens: options.MaxTokens,
            MaxRuntime: options.MaxTurnDuration,
            MaxConcurrentAgents: options.MaxConcurrentSubagents,
            MaxQueuedAgents: options.MaxQueuedAgents,
            MaxTotalAgents: options.MaxTotalSubagentsPerRun,
            MaxCostMicrocents: options.MaxCostMicrocents);
    }
}
