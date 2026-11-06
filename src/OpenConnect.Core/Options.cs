using OpenConnect.Abstractions;

namespace OpenConnect.Core.Configuration;

public sealed class OpenConnectOptions
{
    public const string SectionName = "OpenConnect";
    public AgentRegistryOptions Agents { get; } = new();
    public PrivacyOptions Privacy { get; } = new();
}

public sealed class AgentRegistryOptions
{
    public AgentExecutionMode DefaultExecutionMode { get; set; } = AgentExecutionMode.Interactive;
    public bool RegisterLeadAgent { get; set; } = true;
}

public sealed class PrivacyOptions
{
    public bool Enabled { get; set; } = true;
    public FirewallMode Mode { get; set; } = FirewallMode.StrictLocal;
    public FirewallFailureAction OnFailure { get; set; } = FirewallFailureAction.Deny;
    public DetectorOptions Rules { get; } = new() { Enabled = true };
    public DetectorOptions GlinerPii { get; } = new();
    public DetectorOptions DebertaInjection { get; } = new();
    public DetectorOptions Qwen3Guard { get; } = new();
    public DetectorOptions PaddleOcr { get; } = new();
    public DetectorOptions YuNet { get; } = new();
}

public sealed class DetectorOptions
{
    public bool Enabled { get; set; }
    public string? ModelId { get; set; }
    public string? Revision { get; set; }
    public string? ModelDirectory { get; set; }
    public DetectorDevice Device { get; set; } = DetectorDevice.Cpu;
    public DetectorLoadPolicy LoadPolicy { get; set; } = DetectorLoadPolicy.Startup;
    public int MaxConcurrentRequests { get; set; } = 1;
    public int? MemoryBudgetMb { get; set; }
    public double? Threshold { get; set; }
}
