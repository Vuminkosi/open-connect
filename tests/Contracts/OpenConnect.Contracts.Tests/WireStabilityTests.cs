using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenConnect.Abstractions;
using Xunit;

namespace OpenConnect.Contracts.Tests;

/// <summary>
/// Cross-TFM wire-contract stability harness (CF-REQ-026/027).
/// Pins the canonical event envelope bytes and exact kebab-case wire strings
/// so silent drift fails the build.
/// </summary>
/// <remarks>
/// <para>
/// Uses <see cref="JsonObject"/> construction with fixed insertion order for the
/// golden/mirror assertions below. Typed-converter coverage of the real
/// <c>CanonicalAgentEvent</c>/<c>KebabCaseStringEnumConverter</c> Abstractions code
/// (CF-IMP-005d) lives in the partial typed block at the end of this file.
/// </para>
/// <para>
/// The contract schema version asserted here is <c>openconnect.v1</c>, taken
/// from <c>contracts/jsonschema/event.json</c> (<c>const</c>) and
/// <c>CanonicalAgentEvent.CurrentSchemaVersion</c>.
/// </para>
/// </remarks>
public sealed partial class WireStabilityTests
{
    private static readonly JsonSerializerOptions FixedOptions = new()
    {
        WriteIndented = false,
    };

    // Representative canonical envelope values (contracts/jsonschema/event.json).
    private const string SchemaVersion = "openconnect.v1";
    private const string EventId = "123e4567-e89b-42d3-a456-426614174000";
    private const string SessionId = "123e4567-e89b-42d3-a456-426614174001";
    private const string EventType = "task.accepted";
    private const long Sequence = 1;
    private const string OccurredAt = "2026-01-01T00:00:00Z";

    private static readonly string[] EventRequiredFields =
    [
        "schemaVersion", "eventId", "eventType", "sessionId",
        "sequence", "occurredAt", "payload",
    ];

    [Fact]
    public void EventEnvelope_MatchesCheckedInGoldenFile_ByteForByte()
    {
        var goldenPath = FindGoldenFile("event-envelope.v1.golden.json");
        var actual = BuildCanonicalEnvelopeJson();

        var allowRegenerate = string.Equals(Environment.GetEnvironmentVariable("OPENCONNECT_REGENERATE_GOLDEN"), "1", StringComparison.Ordinal);
        Assert.True(File.Exists(goldenPath) || allowRegenerate, $"Golden fixture missing: {goldenPath} -- restore it, do not regenerate silently. To regenerate explicitly, set OPENCONNECT_REGENERATE_GOLDEN=1.");

        if (!File.Exists(goldenPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(goldenPath)!);
            File.WriteAllText(goldenPath, actual, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        var golden = File.ReadAllText(goldenPath, Encoding.UTF8);
        Assert.Equal(golden, actual);
    }

    [Fact]
    public void EventGolden_ValidatesAgainstEventContract()
    {
        var goldenPath = FindGoldenFile("event-envelope.v1.golden.json");
        Assert.True(File.Exists(goldenPath), $"Golden fixture missing: {goldenPath} -- restore it, do not regenerate silently");
        var golden = File.ReadAllText(goldenPath, Encoding.UTF8);

        using var document = JsonDocument.Parse(golden);
        ValidateEventEnvelope(document.RootElement);
    }

    [Theory]
    // FirewallMode (task example: StrictLocal -> "strict-local").
    [InlineData("StrictLocal", "strict-local")]
    [InlineData("LocalOnly", "local-only")]
    [InlineData("PolicyControlled", "policy-controlled")]
    [InlineData("Disabled", "disabled")]
    // AgentExecutionMode.
    [InlineData("Interactive", "interactive")]
    [InlineData("Operational", "operational")]
    // AgentActivationMode.
    [InlineData("OnDemand", "on-demand")]
    [InlineData("Scheduled", "scheduled")]
    [InlineData("EventTriggered", "event-triggered")]
    [InlineData("Continuous", "continuous")]
    // AgentRunState (full set).
    [InlineData("Accepted", "accepted")]
    [InlineData("Preparing", "preparing")]
    [InlineData("Running", "running")]
    [InlineData("AwaitingUser", "awaiting-user")]
    [InlineData("AwaitingApproval", "awaiting-approval")]
    [InlineData("Paused", "paused")]
    [InlineData("Completed", "completed")]
    [InlineData("Failed", "failed")]
    [InlineData("CancelRequested", "cancel-requested")]
    [InlineData("Cancelled", "cancelled")]
    [InlineData("OutcomeUnknown", "outcome-unknown")]
    // ToolImpact.
    [InlineData("ReadOnly", "read-only")]
    [InlineData("Write", "write")]
    [InlineData("ExternalSideEffect", "external-side-effect")]
    // ModelExecutionLocation.
    [InlineData("SelfHosted", "self-hosted")]
    [InlineData("RemoteAllowed", "remote-allowed")]
    // SecurityDetectorKind.
    [InlineData("Rules", "rules")]
    [InlineData("GlinerPii", "gliner-pii")]
    [InlineData("DebertaInjection", "deberta-injection")]
    [InlineData("Qwen3Guard", "qwen3-guard")]
    [InlineData("PaddleOcr", "paddle-ocr")]
    [InlineData("YuNet", "yu-net")]
    // DetectorLoadPolicy / DetectorDevice.
    [InlineData("Startup", "startup")]
    [InlineData("LazyOnFirstUse", "lazy-on-first-use")]
    [InlineData("Auto", "auto")]
    [InlineData("Cpu", "cpu")]
    [InlineData("Cuda", "cuda")]
    // SecurityPolicyAction.
    [InlineData("Allow", "allow")]
    [InlineData("Mask", "mask")]
    [InlineData("Redact", "redact")]
    [InlineData("Block", "block")]
    [InlineData("Review", "review")]
    // Scheduling / operational policy.
    [InlineData("Skip", "skip")]
    [InlineData("RunOnce", "run-once")]
    [InlineData("Forbid", "forbid")]
    [InlineData("Queue", "queue")]
    [InlineData("ReplaceAfterFence", "replace-after-fence")]
    [InlineData("RetryWithBackoff", "retry-with-backoff")]
    [InlineData("PauseForReview", "pause-for-review")]
    // Tool policy.
    [InlineData("Inline", "inline")]
    [InlineData("BackgroundDurable", "background-durable")]
    [InlineData("PerInvocation", "per-invocation")]
    [InlineData("PerInvocationOnSideEffect", "per-invocation-on-side-effect")]
    // Transport.
    [InlineData("JetStream", "jet-stream")]
    [InlineData("InMemoryDevelopment", "in-memory-development")]
    // Composer placement digit rule (schema: "legacy-viewport20-vh").
    [InlineData("LegacyViewport20Vh", "legacy-viewport20-vh")]
    [InlineData("BottomRight", "bottom-right")]
    [InlineData("LatestOnly", "latest-only")]
    [InlineData("OnDemandHistory", "on-demand-history")]
    public void KebabCaseWireString_IsStable(string memberName, string expectedWireString)
    {
        Assert.Equal(expectedWireString, ToKebabCase(memberName));
    }

    [Fact]
    public void ComposerSchema_ContainsPinnedWireStrings()
    {
        var schemaPath = Path.Combine(FindRepositoryRoot(), "contracts", "jsonschema", "composer.v1.json");
        using var document = JsonDocument.Parse(File.ReadAllText(schemaPath));
        var properties = document.RootElement.GetProperty("properties");

        AssertStringEnumContains(properties.GetProperty("placement"), "bottom-right");
        AssertStringEnumContains(properties.GetProperty("placement"), "legacy-viewport20-vh");
        AssertStringEnumContains(properties.GetProperty("displayState"), "open");
        AssertStringEnumContains(properties.GetProperty("taskPhase"), "tool-executing");
        AssertStringEnumContains(properties.GetProperty("microphoneState"), "listening-live");
        AssertStringEnumContains(properties.GetProperty("historyPresentation"), "latest-only");
    }

    [Fact]
    public void EventSchema_PinsSchemaVersionAndRequiredFields()
    {
        var schemaPath = Path.Combine(FindRepositoryRoot(), "contracts", "jsonschema", "event.json");
        using var document = JsonDocument.Parse(File.ReadAllText(schemaPath));
        var root = document.RootElement;

        Assert.Equal(SchemaVersion, root.GetProperty("properties").GetProperty("schemaVersion").GetProperty("const").GetString());

        var required = root.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToHashSet(StringComparer.Ordinal);
        foreach (var field in EventRequiredFields)
        {
            Assert.Contains(field, required);
        }
    }

    [Fact]
    public void UnknownEnumString_FailsTypedValidation()
    {
        var allowed = LoadComposerPlacementValues();
        Assert.Contains("bottom-right", allowed);

        var exception = Assert.Throws<JsonException>(() => StrictEnum.Parse("placement", "sideways", allowed));
        Assert.Contains("placement", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingRequiredField_FailsTypedValidation()
    {
        using var document = JsonDocument.Parse(BuildCanonicalEnvelopeJson());
        var stripped = document.RootElement.EnumerateObject()
            .Where(p => !string.Equals(p.Name, "eventId", StringComparison.Ordinal))
            .Aggregate(new JsonObject(), (obj, p) =>
            {
                obj.Add(p.Name, JsonNode.Parse(p.Value.GetRawText()));
                return obj;
            });
        using var strippedDocument = JsonDocument.Parse(stripped.ToJsonString(FixedOptions));

        var exception = Assert.Throws<JsonException>(() => ValidateEventEnvelope(strippedDocument.RootElement));
        Assert.Contains("eventId", exception.Message, StringComparison.Ordinal);
    }

    private static string BuildCanonicalEnvelopeJson()
    {
        var envelope = new JsonObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["eventId"] = EventId,
            ["eventType"] = EventType,
            ["sessionId"] = SessionId,
            ["sequence"] = Sequence,
            ["occurredAt"] = OccurredAt,
            ["correlationId"] = JsonValue.Create((string?)null),
            ["payload"] = new JsonObject { ["taskId"] = "task-1" },
        };
        return envelope.ToJsonString(FixedOptions);
    }

    private static void ValidateEventEnvelope(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Event envelope must be a JSON object.");
        }

        foreach (var field in EventRequiredFields)
        {
            if (!root.TryGetProperty(field, out _))
            {
                throw new JsonException($"Event envelope is missing required field '{field}'.");
            }
        }

        var schemaVersion = root.GetProperty("schemaVersion").GetString();
        if (!string.Equals(schemaVersion, SchemaVersion, StringComparison.Ordinal))
        {
            throw new JsonException($"Event envelope schemaVersion must be '{SchemaVersion}'.");
        }

        if (root.GetProperty("sequence").GetInt64() <= 0)
        {
            throw new JsonException("Event envelope sequence must be positive.");
        }
    }

    private static HashSet<string> LoadComposerPlacementValues()
    {
        var schemaPath = Path.Combine(FindRepositoryRoot(), "contracts", "jsonschema", "composer.v1.json");
        using var document = JsonDocument.Parse(File.ReadAllText(schemaPath));
        return document.RootElement
            .GetProperty("properties")
            .GetProperty("placement")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(e => e.GetString()!)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static void AssertStringEnumContains(JsonElement schemaProperty, string expected)
    {
        var values = schemaProperty.GetProperty("enum").EnumerateArray().Select(e => e.GetString());
        Assert.Contains(expected, values);
    }

    /// <summary>Mirrors <c>KebabCaseNamingPolicy</c> in OpenConnect.Abstractions.</summary>
    private static string ToKebabCase(string name)
    {
        var result = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c) && i > 0 && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1])))
            {
                result.Append('-');
            }

            result.Append(char.ToLowerInvariant(c));
        }

        return result.ToString();
    }

    private static string FindGoldenFile(string fileName)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "OpenConnect.Contracts.Tests.csproj");
            if (File.Exists(candidate))
            {
                return Path.Combine(directory.FullName, "Fixtures", fileName);
            }
        }

        throw new DirectoryNotFoundException("Could not locate the OpenConnect.Contracts.Tests project directory.");
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "contracts", "jsonschema")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the repository contracts directory.");
    }

    private static class StrictEnum
    {
        public static string Parse(string property, string value, IReadOnlySet<string> allowed)
        {
            if (!allowed.Contains(value))
            {
                throw new JsonException($"Unknown value '{value}' for '{property}'. Allowed: {string.Join(", ", allowed.OrderBy(v => v, StringComparer.Ordinal))}.");
            }

            return value;
        }
    }
    public static TheoryData<object, string> StableEnumWireCases => new()
    {
        { AgentExecutionMode.Interactive, "interactive" },
        { AgentExecutionMode.Operational, "operational" },
        { AgentActivationMode.OnDemand, "on-demand" },
        { AgentActivationMode.Scheduled, "scheduled" },
        { AgentActivationMode.EventTriggered, "event-triggered" },
        { AgentActivationMode.Continuous, "continuous" },
        { AgentRunState.Accepted, "accepted" },
        { AgentRunState.AwaitingApproval, "awaiting-approval" },
        { AgentRunState.CancelRequested, "cancel-requested" },
        { AgentRunState.OutcomeUnknown, "outcome-unknown" },
        { RunState.Registered, "registered" },
        { RunState.Queued, "queued" },
        { AgentIdentityMode.ActiveUser, "active-user" },
        { AgentIdentityMode.WorkloadIdentity, "workload-identity" },
        { ToolImpact.ReadOnly, "read-only" },
        { ToolImpact.Write, "write" },
        { ToolImpact.ExternalSideEffect, "external-side-effect" },
        { ModelExecutionLocation.LocalOnly, "local-only" },
        { ModelExecutionLocation.SelfHosted, "self-hosted" },
        { ModelExecutionLocation.RemoteAllowed, "remote-allowed" },
        { FirewallMode.StrictLocal, "strict-local" },
        { FirewallMode.LocalOnly, "local-only" },
        { FirewallMode.PolicyControlled, "policy-controlled" },
        { FirewallMode.Disabled, "disabled" },
        { FirewallFailureAction.Deny, "deny" },
        { FirewallFailureAction.LocalOnlyIfVerified, "local-only-if-verified" },
        { SecurityDetectorKind.Rules, "rules" },
        { SecurityDetectorKind.GlinerPii, "gliner-pii" },
        { SecurityDetectorKind.DebertaInjection, "deberta-injection" },
        { SecurityDetectorKind.Qwen3Guard, "qwen3-guard" },
        { SecurityDetectorKind.PaddleOcr, "paddle-ocr" },
        { SecurityDetectorKind.YuNet, "yu-net" },
        { DetectorLoadPolicy.Startup, "startup" },
        { DetectorLoadPolicy.LazyOnFirstUse, "lazy-on-first-use" },
        { DetectorDevice.Auto, "auto" },
        { DetectorDevice.Cpu, "cpu" },
        { DetectorDevice.Cuda, "cuda" },
        { DetectorState.Disabled, "disabled" },
        { DetectorState.Degraded, "degraded" },
        { SecurityCoverage.StructuredPii, "structured-pii" },
        { SecurityCoverage.SemanticPii, "semantic-pii" },
        { SecurityPolicyAction.Allow, "allow" },
        { SecurityPolicyAction.Block, "block" },
        { SecurityPolicyAction.Review, "review" },
        { SecurityCoverageUnavailableAction.Block, "block" },
        { SecurityCoverageUnavailableAction.LocalOnlyIfVerified, "local-only-if-verified" },
        { SchedulingMisfirePolicy.Skip, "skip" },
        { SchedulingMisfirePolicy.RunOnce, "run-once" },
        { OperationalConcurrencyPolicy.Forbid, "forbid" },
        { OperationalConcurrencyPolicy.ReplaceAfterFence, "replace-after-fence" },
        { OperationalRecoveryPolicy.RetryWithBackoff, "retry-with-backoff" },
        { OperationalRecoveryPolicy.PauseForReview, "pause-for-review" },
        { AgentOrchestrationMode.SupervisorDelegation, "supervisor-delegation" },
        { AgentContextSharingMode.ScopedMemory, "scoped-memory" },
        { AgentMemoryMode.DurableScoped, "durable-scoped" },
        { SessionTurnConcurrency.RejectConflict, "reject-conflict" },
        { AgentResultMergeMode.OrderedTypedResults, "ordered-typed-results" },
        { ToolIdempotency.IdempotencyKeyRequired, "idempotency-key-required" },
        { ToolExecutionMode.Inline, "inline" },
        { ToolExecutionMode.BackgroundDurable, "background-durable" },
        { ToolAcknowledgementMode.NeverForShortTools, "never-for-short-tools" },
        { ToolProgressMode.ReportedAndHeartbeat, "reported-and-heartbeat" },
        { ToolApprovalMode.PerInvocationOnSideEffect, "per-invocation-on-side-effect" },
        { ToolExecutionState.AwaitingApproval, "awaiting-approval" },
        { ToolProgressKind.MeasuredProgress, "measured-progress" },
        { AgentHistoryPolicy.FullWithinBudget, "full-within-budget" },
        { AgentMemoryWritePolicy.ExplicitApproved, "explicit-approved" },
        { OpenConnectTransportMode.JetStream, "jet-stream" },
        { OpenConnectTransportMode.InMemoryDevelopment, "in-memory-development" },
    };
    [Theory]
    [MemberData(nameof(StableEnumWireCases))]
    public void TypedEnum_SerializesToPinnedKebabString(object value, string expected)
    {
        var json = JsonSerializer.Serialize(value, value.GetType());
        Assert.Equal("\"" + expected + "\"", json);
    }
    [Fact]
    public void TypedEnum_EveryMemberRoundTrips()
    {
        foreach (var (enumType, member) in EnumerateStableEnumValues())
        {
            var json = JsonSerializer.Serialize(member, enumType);
            var roundTripped = JsonSerializer.Deserialize(json, enumType);
            Assert.Equal(member, roundTripped);
        }
    }
    [Fact]
    public void TypedEnum_NumericValue_IsRejected_MirroringNumericEnumFixture()
    {
        var fixturePath = Path.Combine(FindRepositoryRoot(), "contracts", "fixtures", "composer", "invalid", "numeric-enum.json");
        using var fixture = JsonDocument.Parse(File.ReadAllText(fixturePath));
        Assert.Equal(JsonValueKind.Number, fixture.RootElement.GetProperty("placement").ValueKind);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<FirewallMode>("0"));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AgentRunState>("1"));
    }
    [Fact]
    public void TypedEnum_UnknownString_IsRejected()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<FirewallMode>("\"sideways\""));
    }
    [Fact]
    public void CanonicalAgentEvent_TypedRoundTrip_PreservesEnvelope()
    {
        var original = new CanonicalAgentEvent(
            CanonicalAgentEvent.CurrentSchemaVersion,
            Guid.Parse("123e4567-e89b-42d3-a456-426614174000"),
            "task.accepted",
            Guid.Parse("123e4567-e89b-42d3-a456-426614174001"),
            1,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new JsonObject { ["taskId"] = "task-1" });
        var json = JsonSerializer.Serialize(original);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("openconnect.v1", root.GetProperty("schemaVersion").GetString());
        Assert.Equal("task.accepted", root.GetProperty("eventType").GetString());
        Assert.Equal(1, root.GetProperty("sequence").GetInt64());
        Assert.Equal("task-1", root.GetProperty("payload").GetProperty("taskId").GetString());
        var roundTripped = JsonSerializer.Deserialize<CanonicalAgentEvent>(json);
        Assert.NotNull(roundTripped);
        Assert.Equal(original.SchemaVersion, roundTripped.SchemaVersion);
        Assert.Equal(original.EventId, roundTripped.EventId);
        Assert.Equal(original.EventType, roundTripped.EventType);
        Assert.Equal(original.SessionId, roundTripped.SessionId);
        Assert.Equal(original.Sequence, roundTripped.Sequence);
        Assert.Equal(original.OccurredAt, roundTripped.OccurredAt);
        Assert.Equal(original.CorrelationId, roundTripped.CorrelationId);
        Assert.Equal(original.Payload.ToJsonString(), roundTripped.Payload.ToJsonString());
    }
    [Fact]
    public void CanonicalAgentEvent_CorrelationId_OmittedWhenNull_EmittedWhenSet()
    {
        // Null correlationId is omitted (WhenWritingNull); the golden explicit null is a JsonObject artifact.
        var withoutCorrelation = new CanonicalAgentEvent(
            CanonicalAgentEvent.CurrentSchemaVersion,
            Guid.Parse("123e4567-e89b-42d3-a456-426614174000"),
            "task.accepted",
            Guid.Parse("123e4567-e89b-42d3-a456-426614174001"),
            1,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new JsonObject { ["taskId"] = "task-1" });
        var jsonWithout = JsonSerializer.Serialize(withoutCorrelation);
        Assert.DoesNotContain("correlationId", jsonWithout, StringComparison.Ordinal);
        var withCorrelation = withoutCorrelation with { CorrelationId = "corr-1" };
        var jsonWith = JsonSerializer.Serialize(withCorrelation);
        var roundTripped = JsonSerializer.Deserialize<CanonicalAgentEvent>(jsonWith);
        Assert.NotNull(roundTripped);
        Assert.Equal("corr-1", roundTripped.CorrelationId);
        Assert.Contains("\"correlationId\":\"corr-1\"", jsonWith, StringComparison.Ordinal);
    }
    private static IEnumerable<(Type EnumType, object Value)> EnumerateStableEnumValues()
    {
        var assembly = typeof(CanonicalAgentEvent).Assembly;
        foreach (var type in assembly.GetTypes().Where(t => t.IsEnum).OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            foreach (var value in Enum.GetValues(type))
            {
                yield return (type, value);
            }
        }
    }
}
