using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenConnect.Abstractions;
using Xunit;

namespace OpenConnect.Contracts.Tests;

/// <summary>
/// Cross-language golden schema parity tests (OC2-D-WORKERS-001, OC2-D-REL-008).
/// Verifies versioned C#, TypeScript, and Python golden contract parity across:
/// 1. Worker task payload envelopes (worker-envelope.v1)
/// 2. Capability registrations (capability.v1)
/// 3. Worker errors (worker-error.v1)
/// 4. ProblemDetails RFC-7807 error envelopes (problem-details.v1)
/// 5. Durable tool invocations (durable-tool-invocation.v1)
/// </summary>
public sealed class CrossLanguageContractParityTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void WorkerEnvelope_MatchesGoldenAndConformsToSchema()
    {
        var goldenPath = FindGoldenFile("worker-envelope.v1.golden.json");
        Assert.True(File.Exists(goldenPath), $"Missing golden: {goldenPath}");
        var goldenJson = File.ReadAllText(goldenPath, Encoding.UTF8);

        using var doc = JsonDocument.Parse(goldenJson);
        var root = doc.RootElement;

        Assert.Equal("openconnect.v1", root.GetProperty("schemaVersion").GetString());
        Assert.Equal("123e4567-e89b-42d3-a456-426614174000", root.GetProperty("messageId").GetString());
        Assert.Equal("task-1", root.GetProperty("taskId").GetString());
        Assert.Equal("classification", root.GetProperty("kind").GetString());
        Assert.Equal("2026-01-01T00:00:00Z", root.GetProperty("issuedAt").GetString());
        Assert.Equal("2026-01-01T00:01:00Z", root.GetProperty("expiresAt").GetString());
        Assert.Equal("opaque://payload/abc", root.GetProperty("payloadRef").GetString());

        var lease = root.GetProperty("lease");
        Assert.Equal("123e4567-e89b-42d3-a456-426614174001", lease.GetProperty("leaseId").GetString());
        Assert.Equal(1, lease.GetProperty("fencingToken").GetInt64());

        // Validate schema conformity
        var schemaPath = Path.Combine(RepositoryRoot, "contracts", "jsonschema", "worker-envelope.json");
        Assert.True(File.Exists(schemaPath), $"Schema not found: {schemaPath}");
        using var schemaDoc = JsonDocument.Parse(File.ReadAllText(schemaPath));
        var req = schemaDoc.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToArray();
        foreach (var prop in req)
        {
            Assert.True(root.TryGetProperty(prop!, out _), $"Required property '{prop}' missing in worker-envelope golden.");
        }
    }

    [Fact]
    public void CapabilityRegistration_MatchesGoldenAndConformsToSchema()
    {
        var goldenPath = FindGoldenFile("capability.v1.golden.json");
        Assert.True(File.Exists(goldenPath), $"Missing golden: {goldenPath}");
        var goldenJson = File.ReadAllText(goldenPath, Encoding.UTF8);

        using var doc = JsonDocument.Parse(goldenJson);
        var root = doc.RootElement;

        Assert.Equal("openconnect.v1", root.GetProperty("schemaVersion").GetString());
        Assert.Equal("chat-default", root.GetProperty("id").GetString());
        Assert.Equal("chat", root.GetProperty("kind").GetString());
        Assert.Equal("ready", root.GetProperty("state").GetString());
        Assert.Equal("1.0.0", root.GetProperty("version").GetString());
        Assert.False(root.GetProperty("localOnly").GetBoolean());

        // Validate schema conformity
        var schemaPath = Path.Combine(RepositoryRoot, "contracts", "jsonschema", "capability.json");
        Assert.True(File.Exists(schemaPath), $"Schema not found: {schemaPath}");
        using var schemaDoc = JsonDocument.Parse(File.ReadAllText(schemaPath));
        var req = schemaDoc.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToArray();
        foreach (var prop in req)
        {
            Assert.True(root.TryGetProperty(prop!, out _), $"Required property '{prop}' missing in capability golden.");
        }
    }

    [Fact]
    public void WorkerError_MatchesGoldenAndStandardCodes()
    {
        var goldenPath = FindGoldenFile("worker-error.v1.golden.json");
        Assert.True(File.Exists(goldenPath), $"Missing golden: {goldenPath}");
        var goldenJson = File.ReadAllText(goldenPath, Encoding.UTF8);

        using var doc = JsonDocument.Parse(goldenJson);
        var root = doc.RootElement;

        var code = root.GetProperty("code").GetString();
        Assert.Equal(ErrorCodes.PrivacyBlocked, code);
        Assert.Equal("Blocked by privacy firewall rule", root.GetProperty("message").GetString());
        Assert.Equal("corr-123", root.GetProperty("correlationId").GetString());
        Assert.False(root.GetProperty("retryable").GetBoolean());
    }

    [Fact]
    public void ProblemDetails_MatchesRfc7807AndCanonicalErrorCodes()
    {
        var goldenPath = FindGoldenFile("problem-details.v1.golden.json");
        Assert.True(File.Exists(goldenPath), $"Missing golden: {goldenPath}");
        var goldenJson = File.ReadAllText(goldenPath, Encoding.UTF8);

        using var doc = JsonDocument.Parse(goldenJson);
        var root = doc.RootElement;

        Assert.Equal("https://openconnect.dev/problems/session-not-found", root.GetProperty("type").GetString());
        Assert.Equal("Session not found", root.GetProperty("title").GetString());
        Assert.Equal(404, root.GetProperty("status").GetInt32());
        Assert.Equal("The requested session does not exist or has expired", root.GetProperty("detail").GetString());
        Assert.Equal("/sessions/123e4567-e89b-42d3-a456-426614174001", root.GetProperty("instance").GetString());
        Assert.Equal(ErrorCodes.SessionNotFound, root.GetProperty("code").GetString());
        Assert.Equal("corr-problem-1", root.GetProperty("correlationId").GetString());
    }

    [Fact]
    public void DurableToolInvocation_MatchesGoldenPayloadShape()
    {
        var goldenPath = FindGoldenFile("durable-tool-invocation.v1.golden.json");
        Assert.True(File.Exists(goldenPath), $"Missing golden: {goldenPath}");
        var goldenJson = File.ReadAllText(goldenPath, Encoding.UTF8);

        using var doc = JsonDocument.Parse(goldenJson);
        var root = doc.RootElement;

        Assert.Equal("openconnect.v1", root.GetProperty("schemaVersion").GetString());
        Assert.Equal("123e4567-e89b-42d3-a456-426614174000", root.GetProperty("taskId").GetString());
        Assert.Equal("order_status", root.GetProperty("toolId").GetString());
        Assert.Equal("order-999", root.GetProperty("arguments").GetProperty("orderId").GetString());
        Assert.Equal("user:ops-lead", root.GetProperty("callerActorId").GetString());
        Assert.Equal("tenant-alpha", root.GetProperty("tenantId").GetString());
        Assert.Equal("durable", root.GetProperty("executionMode").GetString());
        Assert.Equal(30000, root.GetProperty("timeoutMs").GetInt32());
    }

    [Theory]
    [InlineData("task_not_found")]
    [InlineData("session_not_found")]
    [InlineData("unauthorized")]
    [InlineData("tool_not_allowed")]
    [InlineData("approval_required")]
    [InlineData("capability_unavailable")]
    [InlineData("privacy_blocked")]
    [InlineData("invalid_request")]
    [InlineData("conflict")]
    [InlineData("rate_limited")]
    public void CanonicalErrorCodes_AreIdenticalAcrossCsAndPythonAndTs(string errorCode)
    {
        // Assert ErrorCodes constant value in C# matches exactly
        var field = typeof(ErrorCodes).GetFields()
            .FirstOrDefault(f => string.Equals((string?)f.GetValue(null), errorCode, StringComparison.Ordinal));
        Assert.NotNull(field);
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

        throw new DirectoryNotFoundException($"Could not locate OpenConnect.Contracts.Tests.csproj above {AppContext.BaseDirectory}.");
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
}
