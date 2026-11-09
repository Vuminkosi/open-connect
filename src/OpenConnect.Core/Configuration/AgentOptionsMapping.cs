using OpenConnect.Abstractions;

namespace OpenConnect.Core.Configuration;

/// <summary>Maps validated agent registry settings to the immutable limits for one run.</summary>
/// <remarks>
/// Mapping table: <see cref="AgentBudget.MaxToolCalls"/> copies
/// <see cref="AgentRegistryOptions.MaxTotalToolCalls"/>; <see cref="AgentBudget.MaxModelCalls"/>
/// copies <see cref="AgentRegistryOptions.MaxModelCalls"/>; <see cref="AgentBudget.MaxTokens"/>
/// uses <see cref="long.MaxValue"/> because <see cref="AgentRegistryOptions"/> has no token
/// limit; <see cref="AgentBudget.MaxRuntime"/> copies <see cref="AgentRegistryOptions.MaxTurnDuration"/>;
/// <see cref="AgentBudget.MaxConcurrentAgents"/> copies
/// <see cref="AgentRegistryOptions.MaxConcurrentSubagents"/>;
/// <see cref="AgentBudget.MaxQueuedAgents"/> uses <see cref="int.MaxValue"/> because
/// <see cref="AgentRegistryOptions"/> has no queued-agent limit; and
/// <see cref="AgentBudget.MaxTotalAgents"/> copies
/// <see cref="AgentRegistryOptions.MaxTotalSubagentsPerRun"/>. The options field list has no
/// token or queue caps, so unconfigured dimensions must not veto configured ones. Explicitly
/// constructed <see cref="AgentBudget"/> instances with zero limits retain their fail-closed
/// meaning in the ledger; hosts needing token or queue caps should construct an
/// <see cref="AgentBudget"/> directly. Delegation depth remains enforced by the delegation policy
/// layer and is not a run-budget dimension. The unbounded token sentinel follows the existing
/// <c>MaxRuntimeTicks</c> mapping in <c>OrchestrationAdapters.CreateChild</c>.
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
            MaxTokens: long.MaxValue,
            MaxRuntime: options.MaxTurnDuration,
            MaxConcurrentAgents: options.MaxConcurrentSubagents,
            MaxQueuedAgents: int.MaxValue,
            MaxTotalAgents: options.MaxTotalSubagentsPerRun);
    }
}
