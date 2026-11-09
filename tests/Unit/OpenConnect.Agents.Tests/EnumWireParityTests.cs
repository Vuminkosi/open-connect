using System.Text.Json;
using OpenConnect.Abstractions;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class EnumWireParityTests
{
    private static readonly (Type Type, string[] Values)[] ExpectedEnums =
    [
        (typeof(AgentExecutionMode), ["interactive", "operational"]),
        (typeof(AgentOrchestrationMode), ["direct", "sequential", "parallel", "handoff", "supervisor-delegation"]),
        (typeof(AgentActivationMode), ["on-demand", "scheduled", "event-triggered", "continuous"]),
        (typeof(AgentIdentityMode), ["active-user", "workload-identity"]),
        (typeof(AgentContextSharingMode), ["none", "task-summary", "relevant-history", "approved-artifacts", "scoped-memory"]),
        (typeof(AgentMemoryMode), ["disabled", "session-only", "durable-scoped", "semantic-optional"]),
        (typeof(SessionTurnConcurrency), ["serialize", "queue", "reject-conflict"]),
        (typeof(AgentResultMergeMode), ["lead-synthesizes", "ordered-typed-results", "explicit-reducer"]),
        (typeof(ToolImpact), ["read-only", "write", "external-side-effect"]),
        (typeof(ToolIdempotency), ["safe-to-retry", "idempotency-key-required", "non-idempotent"]),
        (typeof(ToolExecutionMode), ["inline", "background-durable"]),
        (typeof(ToolAcknowledgementMode), ["automatic", "always", "never-for-short-tools"]),
        (typeof(ToolProgressMode), ["none", "reported", "heartbeat-only", "reported-and-heartbeat"]),
        (typeof(ToolApprovalMode), ["none", "per-invocation", "per-invocation-on-side-effect"]),
        (typeof(ToolExecutionState), ["proposed", "awaiting-approval", "accepted", "queued", "starting", "running", "finalizing", "completed", "failed", "cancel-requested", "cancelled", "outcome-unknown"]),
        (typeof(ToolProgressKind), ["phase", "measured-progress", "heartbeat", "diagnostic-summary"]),
        (typeof(AgentRunState), ["accepted", "preparing", "running", "awaiting-user", "awaiting-approval", "paused", "completed", "failed", "cancel-requested", "cancelled", "outcome-unknown"]),
        (typeof(AgentHistoryPolicy), ["recent-turns", "bounded-summary", "full-within-budget", "provider-managed"]),
        (typeof(AgentMemoryWritePolicy), ["deny-by-default", "explicit-approved", "application-policy"]),
        (typeof(OpenConnectTransportMode), ["jet-stream", "in-memory-development"])
    ];

    public static IEnumerable<object[]> EnumValues()
    {
        foreach (var (type, values) in ExpectedEnums)
        {
            var members = Enum.GetValues(type);
            Assert.Equal(members.Length, values.Length);
            for (var i = 0; i < members.Length; i++)
            {
                yield return [type, members.GetValue(i)!, values[i]];
            }
        }
    }

    [Theory]
    [MemberData(nameof(EnumValues))]
    public void Serialize_UsesStableKebabCaseWireValue(Type enumType, object value, string expected)
    {
        Assert.Equal($"\"{expected}\"", JsonSerializer.Serialize(value, enumType));
    }

    public static IEnumerable<object[]> EnumTypes()
    {
        foreach (var (type, _) in ExpectedEnums)
            yield return [type];
    }

    [Theory]
    [MemberData(nameof(EnumTypes))]
    public void Deserialize_RejectsUnknownStringAndNumericValues(Type enumType)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("\"unknown-value\"", enumType));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("0", enumType));
    }
}
