using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenConnect.Abstractions;

/// <summary>Converts enum member names to strict kebab-case JSON strings.</summary>
public sealed class KebabCaseStringEnumConverter<TEnum> : JsonStringEnumConverter<TEnum> where TEnum : struct, Enum
{
    public KebabCaseStringEnumConverter() : base(KebabCaseNamingPolicy.Instance, allowIntegerValues: false) { }
}

internal sealed class KebabCaseNamingPolicy : JsonNamingPolicy
{
    public static KebabCaseNamingPolicy Instance { get; } = new();
    public override string ConvertName(string name)
    {
        var result = new System.Text.StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c) && i > 0 && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]))) result.Append('-');
            result.Append(char.ToLowerInvariant(c));
        }
        return result.ToString();
    }
}

[JsonConverter(typeof(KebabCaseStringEnumConverter<TransportMode>))]
public enum TransportMode { JetStream, InMemoryDevelopment }
[JsonConverter(typeof(KebabCaseStringEnumConverter<AgentExecutionMode>))]
public enum AgentExecutionMode { Interactive, Operational }
[JsonConverter(typeof(KebabCaseStringEnumConverter<ActivationMode>))]
public enum ActivationMode { OnDemand, Scheduled, Continuous, EventTriggered }
[JsonConverter(typeof(KebabCaseStringEnumConverter<RunState>))]
public enum RunState { Registered, Queued, Starting, Running, Waiting, Paused, Completed, Failed, Cancelled, Stale }
[JsonConverter(typeof(KebabCaseStringEnumConverter<IdentityMode>))]
public enum IdentityMode { ActiveUser, WorkloadIdentity }
[JsonConverter(typeof(KebabCaseStringEnumConverter<ToolImpact>))]
public enum ToolImpact { ReadOnly, Write, ExternalSideEffect }
[JsonConverter(typeof(KebabCaseStringEnumConverter<ModelExecutionLocation>))]
public enum ModelExecutionLocation { Local, Remote }
[JsonConverter(typeof(KebabCaseStringEnumConverter<FirewallMode>))]
public enum FirewallMode { StrictLocal, LocalOnly, PolicyControlled, Disabled }
[JsonConverter(typeof(KebabCaseStringEnumConverter<FirewallFailureAction>))]
public enum FirewallFailureAction { Deny, LocalOnlyIfVerified }
[JsonConverter(typeof(KebabCaseStringEnumConverter<SecurityDetectorKind>))]
public enum SecurityDetectorKind { Rules, GlinerPii, DebertaInjection, Qwen3Guard, PaddleOcr, YuNet }
[JsonConverter(typeof(KebabCaseStringEnumConverter<DetectorLoadPolicy>))]
public enum DetectorLoadPolicy { Startup, LazyOnFirstUse }
[JsonConverter(typeof(KebabCaseStringEnumConverter<DetectorDevice>))]
public enum DetectorDevice { Auto, Cpu, Cuda }
[JsonConverter(typeof(KebabCaseStringEnumConverter<DetectorState>))]
public enum DetectorState { Disabled, Loading, Ready, Degraded, Failed }
[JsonConverter(typeof(KebabCaseStringEnumConverter<SecurityCoverage>))]
public enum SecurityCoverage { Complete, Partial, Unavailable }
[JsonConverter(typeof(KebabCaseStringEnumConverter<SecurityPolicyAction>))]
public enum SecurityPolicyAction { Allow, Mask, Redact, Block, Review }
[JsonConverter(typeof(KebabCaseStringEnumConverter<CoverageUnavailableAction>))]
public enum CoverageUnavailableAction { Deny, AllowLocalOnly }
[JsonConverter(typeof(KebabCaseStringEnumConverter<SchedulingMisfire>))]
public enum SchedulingMisfire { Skip, RunOnce, CatchUp }
[JsonConverter(typeof(KebabCaseStringEnumConverter<OperationalConcurrency>))]
public enum OperationalConcurrency { Allow, Deny }
[JsonConverter(typeof(KebabCaseStringEnumConverter<OperationalRecovery>))]
public enum OperationalRecovery { Retry, Resume, Restart, Fail }
