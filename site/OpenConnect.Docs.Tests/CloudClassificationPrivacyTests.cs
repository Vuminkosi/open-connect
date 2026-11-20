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
}
