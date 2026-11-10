using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Core.Configuration;

namespace OpenConnect.Core;

public sealed class OpenConnectOptionsValidator : IValidateOptions<OpenConnectOptions>
{
    public ValidateOptionsResult Validate(string? name, OpenConnectOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();
        if (!Enum.IsDefined(options.Agents.DefaultExecutionMode)) errors.Add("Agents:DefaultExecutionMode is not a supported value.");
        var agents = options.Agents;
        if (!Enum.IsDefined(agents.IdentityMode)) errors.Add("Agents:IdentityMode is not a supported value.");
        if (!Enum.IsDefined(agents.OrchestrationMode)) errors.Add("Agents:OrchestrationMode is not a supported value.");
        if (!Enum.IsDefined(agents.ContextSharing)) errors.Add("Agents:ContextSharing is not a supported value.");
        if (!Enum.IsDefined(agents.SessionHistory)) errors.Add("Agents:SessionHistory is not a supported value.");
        if (!Enum.IsDefined(agents.MemoryMode)) errors.Add("Agents:MemoryMode is not a supported value.");
        if (!Enum.IsDefined(agents.Concurrency)) errors.Add("Agents:Concurrency is not a supported value.");
        if (agents.MaxDelegationDepth is < 0 or > 2) errors.Add("Agents:MaxDelegationDepth must be between 0 and 2.");
        if (agents.MaxConcurrentSubagents < 0) errors.Add("Agents:MaxConcurrentSubagents cannot be negative.");
        if (agents.MaxTotalSubagentsPerRun < 0) errors.Add("Agents:MaxTotalSubagentsPerRun cannot be negative.");
        if (agents.MaxTotalToolCalls < 0) errors.Add("Agents:MaxTotalToolCalls cannot be negative.");
        if (agents.MaxModelCalls < 0) errors.Add("Agents:MaxModelCalls cannot be negative.");
        if (agents.MaxTokens < 0) errors.Add("Agents:MaxTokens cannot be negative.");
        if (agents.MaxQueuedAgents < 0) errors.Add("Agents:MaxQueuedAgents cannot be negative.");
        if (agents.MaxCostMicrocents < 0) errors.Add("Agents:MaxCostMicrocents cannot be negative.");
        if (agents.MicrocentsPerThousandTokens < 0) errors.Add("Agents:MicrocentsPerThousandTokens cannot be negative.");
        if (agents.MaxTurnDuration <= TimeSpan.Zero) errors.Add("Agents:MaxTurnDuration must be positive.");
        if (agents.MaxToolDuration <= TimeSpan.Zero) errors.Add("Agents:MaxToolDuration must be positive.");
        if (agents.LongRunningThreshold <= TimeSpan.Zero) errors.Add("Agents:LongRunningThreshold must be positive.");
        if (agents.HeartbeatInterval <= TimeSpan.Zero) errors.Add("Agents:HeartbeatInterval must be positive.");
        if (agents.ProgressCoalescingInterval <= TimeSpan.Zero) errors.Add("Agents:ProgressCoalescingInterval must be positive.");
        if (agents.DefaultExecutionMode == AgentExecutionMode.Operational && agents.IdentityMode == AgentIdentityMode.ActiveUser)
            errors.Add("Operational agents cannot use ActiveUser identity.");
        if (!Enum.IsDefined(options.Privacy.Mode)) errors.Add("Privacy:Mode is not a supported value.");
        if (!Enum.IsDefined(options.Privacy.OnFailure)) errors.Add("Privacy:OnFailure is not a supported value.");

        foreach (var (namePart, detector) in Detectors(options.Privacy))
        {
            if (!Enum.IsDefined(detector.Device)) errors.Add($"Privacy:{namePart}:Device is not a supported value.");
            if (!Enum.IsDefined(detector.LoadPolicy)) errors.Add($"Privacy:{namePart}:LoadPolicy is not a supported value.");
            if (detector.MaxConcurrentRequests < 1) errors.Add($"Privacy:{namePart}:MaxConcurrentRequests must be at least 1.");
            if (detector.MemoryBudgetMb is <= 0) errors.Add($"Privacy:{namePart}:MemoryBudgetMb must be positive when specified.");
            if (detector.Threshold is < 0 or > 1) errors.Add($"Privacy:{namePart}:Threshold must be between 0 and 1.");
        }

        if (options.Privacy.Enabled && options.Privacy.Mode == FirewallMode.Disabled)
            errors.Add("Privacy:Mode cannot be disabled while Privacy:Enabled is true. Set Privacy:Enabled=false explicitly to disable privacy enforcement.");
        if (!options.Privacy.Enabled && options.Privacy.Mode != FirewallMode.Disabled)
            errors.Add("Privacy:Enabled=false requires Privacy:Mode=Disabled so the explicit security choice is unambiguous.");
        if (options.Privacy.Enabled && options.Privacy.Mode == FirewallMode.StrictLocal &&
            !options.Privacy.Rules.Enabled && !options.Privacy.GlinerPii.Enabled)
            errors.Add("Strict-local privacy requires at least one PII detector; enable Rules or GLiNER PII.");

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static IEnumerable<(string Name, DetectorOptions Options)> Detectors(PrivacyOptions options)
    {
        yield return ("Rules", options.Rules);
        yield return ("GlinerPii", options.GlinerPii);
        yield return ("DebertaInjection", options.DebertaInjection);
        yield return ("Qwen3Guard", options.Qwen3Guard);
        yield return ("PaddleOcr", options.PaddleOcr);
        yield return ("YuNet", options.YuNet);
    }
}
