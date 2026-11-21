using System.Text.Json;
using OpenConnect.Docs.Cloud;
using OpenConnect.Privacy;
using OpenConnect.Privacy.Rules;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class CloudKnowledgeEmbeddingPrivacyLimitsTests
{
    [Fact]
    public async Task LargeEmbeddingBatchIsFullyInspectedWithinItsDedicatedBudget()
    {
        var identity = new CloudCredentialIdentity(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Development", new HashSet<string>());
        var vectors = Enumerable.Range(0, 32).Select(_ => new { vector = Enumerable.Range(0, 768).Select(i => i == 0 ? 1.0 : 0.0).ToArray(), norm = 1.0, truncated = false }).ToArray();
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { schemaVersion = "openconnect.v1", taskId = Guid.NewGuid(),
            kind = "knowledge.embed", result = new { model_id = "model", fingerprint = new string('a', 64), dimension = 768,
                space_id = new string('b', 64), items = vectors } }));
        var detector = new AllowDetector();
        var assessment = await new CloudJsonPrivacyService(detector)
            .EvaluateWithEvidenceAsync(identity, Guid.NewGuid(), document.RootElement, CancellationToken.None, embeddingResponse: true);
        Assert.True(assessment.Decision.Allowed);
        Assert.True(detector.Calls > 50000);
        var rules = await new CloudJsonPrivacyService(new RulesSecurityDetector())
            .EvaluateWithEvidenceAsync(identity, Guid.NewGuid(), document.RootElement, CancellationToken.None, embeddingResponse: true);
        Assert.True(rules.Decision.Allowed);
    }

    [Fact]
    public async Task MaximumEmbeddingVectorsAreInspectedAndSensitiveMetadataIsBlocked()
    {
        var identity = new CloudCredentialIdentity(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Development", new HashSet<string>());
        var vector = Enumerable.Range(0, 8192).Select(i => i == 0 ? 1.0 : 0.0).ToArray();
        var items = Enumerable.Range(0, 32).Select(_ => new { vector, norm = 1.0, truncated = false }).ToArray();
        using var clean = JsonDocument.Parse(JsonSerializer.Serialize(new { result = new { model_id = "model", items } }));
        var detector = new AllowDetector();
        var result = await new CloudJsonPrivacyService(detector)
            .EvaluateWithEvidenceAsync(identity, Guid.NewGuid(), clean.RootElement, CancellationToken.None, embeddingResponse: true);
        Assert.True(result.Decision.Allowed);
        Assert.True(detector.Calls > 262144);
        var rulesResult = await new CloudJsonPrivacyService(new RulesSecurityDetector())
            .EvaluateWithEvidenceAsync(identity, Guid.NewGuid(), clean.RootElement, CancellationToken.None, embeddingResponse: true);
        Assert.True(rulesResult.Decision.Allowed);
        using var sensitive = JsonDocument.Parse(JsonSerializer.Serialize(new { result = new { model_id = "person@example.test", items } }));
        var blocked = await new CloudJsonPrivacyService(new RulesSecurityDetector())
            .EvaluateWithEvidenceAsync(identity, Guid.NewGuid(), sensitive.RootElement, CancellationToken.None, embeddingResponse: true);
        Assert.Equal("privacy_blocked", blocked.Decision.Code);
    }

    private sealed class AllowDetector : ISecurityDetector
    {
        public int Calls { get; private set; }
        public ValueTask<DetectorResult> DetectAndSanitizeAsync(string content, EgressContext context, CancellationToken cancellationToken = default)
        { Calls++; return ValueTask.FromResult(new DetectorResult(true, content, [])); }
    }
}
