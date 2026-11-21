using OpenConnect.Docs.Cloud;
using OpenConnect.Privacy;
using OpenConnect.Privacy.Rules;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class CloudClassificationPrivacyTests
{
    private static CloudCredentialIdentity Identity() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Development", new HashSet<string> { "classification.classify" });

    [Fact]
    public async Task CloudRulesBlockDetectedContentWithoutReturningIt()
    {
        var service = new CloudClassificationPrivacyService(new RulesSecurityDetector());
        var identity = Identity();
        Assert.Equal(new CloudPrivacyDecision(true, "allowed"), await service.EvaluateAsync(identity, Guid.NewGuid(), "A harmless message", CancellationToken.None));
        var blocked = await service.EvaluateAsync(identity, Guid.NewGuid(), "contact person@example.com", CancellationToken.None);
        Assert.Equal(new CloudPrivacyDecision(false, "privacy_blocked"), blocked);
        Assert.DoesNotContain("person@example.com", blocked.ToString());
        Assert.Equal("invalid_request", (await service.EvaluateAsync(identity, Guid.Empty, "text", CancellationToken.None)).Code);
    }

    [Fact]
    public async Task MissingOrFailedDetectorFailsClosed()
    {
        var identity = Identity();
        var absent = new CloudClassificationPrivacyService(null);
        var failed = new CloudClassificationPrivacyService(new FailedDetector());
        var changed = new CloudClassificationPrivacyService(new ChangedDetector());
        Assert.Equal(new CloudPrivacyDecision(false, "detector_unavailable"), await absent.EvaluateAsync(identity, Guid.NewGuid(), "text", CancellationToken.None));
        Assert.Equal(new CloudPrivacyDecision(false, "detector_unavailable"), await failed.EvaluateAsync(identity, Guid.NewGuid(), "text", CancellationToken.None));
        Assert.Equal(new CloudPrivacyDecision(false, "privacy_blocked"), await changed.EvaluateAsync(identity, Guid.NewGuid(), "text", CancellationToken.None));
    }

    [Fact]
    public async Task Assessment_counts_only_allowlisted_categories_without_sensitive_values()
    {
        var service = new CloudClassificationPrivacyService(new RulesSecurityDetector());
        var result = await service.EvaluateWithEvidenceAsync(Identity(), Guid.NewGuid(),
            "Email person@example.test and other@example.test, phone +1-555-867-5309", CancellationToken.None);
        Assert.False(result.Decision.Allowed);
        Assert.Contains(result.Categories, item => item.Category == "EMAIL" && item.Count == 2);
        Assert.Contains(result.Categories, item => item.Category == "PHONE" && item.Count == 1);
        Assert.DoesNotContain("person@example.test", System.Text.Json.JsonSerializer.Serialize(result), StringComparison.Ordinal);
        var wire = System.Text.Json.JsonSerializer.Serialize(result.Decision, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Equal("{\"allowed\":false,\"code\":\"privacy_blocked\"}", wire);
    }

    [Fact]
    public async Task Assessment_maps_untrusted_detector_labels_to_other()
    {
        var service = new CloudClassificationPrivacyService(new UntrustedCategoryDetector());
        var result = await service.EvaluateWithEvidenceAsync(Identity(), Guid.NewGuid(), "secret", CancellationToken.None);
        Assert.Equal("OTHER", Assert.Single(result.Categories).Category);
        Assert.DoesNotContain("private-label", System.Text.Json.JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public void Category_evidence_is_bounded_and_rejects_unknown_or_duplicate_labels()
    {
        var json = CloudPrivacyCategories.Encode([new("EMAIL", 2), new("PHONE", 1)], "privacy_blocked");
        Assert.Equal(2, CloudPrivacyCategories.Decode(json, "privacy_blocked")!.Count);
        Assert.Null(CloudPrivacyCategories.Decode(null, "privacy_blocked"));
        Assert.Throws<ArgumentException>(() => CloudPrivacyCategories.Encode([new("private-value", 1)], "privacy_blocked"));
        Assert.Throws<ArgumentException>(() => CloudPrivacyCategories.Encode([new("EMAIL", 1), new("EMAIL", 1)], "privacy_blocked"));
        Assert.Throws<ArgumentException>(() => CloudPrivacyCategories.Encode([new("EMAIL", 1)], "allowed"));
        Assert.Throws<ArgumentException>(() => CloudPrivacyCategories.Decode("{\"EMAIL\":1,\"EMAIL\":2}", "privacy_blocked"));
    }

    private sealed class FailedDetector : ISecurityDetector
    {
        public ValueTask<DetectorResult> DetectAndSanitizeAsync(string content, EgressContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("private diagnostic");
    }

    private sealed class ChangedDetector : ISecurityDetector
    {
        public ValueTask<DetectorResult> DetectAndSanitizeAsync(string content, EgressContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new DetectorResult(true, "changed", []));
    }

    private sealed class UntrustedCategoryDetector : ISecurityDetector
    {
        public ValueTask<DetectorResult> DetectAndSanitizeAsync(string content, EgressContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new DetectorResult(true, "[MASKED]", [new EntitySpan(0, 6, "private-label")]));
    }
}
