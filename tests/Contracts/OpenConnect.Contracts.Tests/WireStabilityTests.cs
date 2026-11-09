using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace OpenConnect.Contracts.Tests;

/// <summary>
/// Cross-TFM wire-contract stability harness (CF-REQ-026/027).
/// Pins the canonical event envelope bytes and exact kebab-case wire strings
/// so silent drift fails the build.
/// </summary>
/// <remarks>
/// <para>
/// Uses <see cref="JsonObject"/> construction with fixed insertion order because
/// the Contracts test project has no reference to OpenConnect.Abstractions
/// (adding one is out of scope here). Typed-converter coverage of
/// <c>CanonicalAgentEvent</c>/<c>KebabCaseStringEnumConverter</c> is deferred to
/// CF-IMP-005d, which can add that ProjectReference.
/// </para>
/// <para>
/// The contract schema version asserted here is <c>openconnect.v1</c>, taken
/// from <c>contracts/jsonschema/event.json</c> (<c>const</c>) and
/// <c>CanonicalAgentEvent.CurrentSchemaVersion</c>.
/// </para>
/// </remarks>
public sealed class WireStabilityTests
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
}
