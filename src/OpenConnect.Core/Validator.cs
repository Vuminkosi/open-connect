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
