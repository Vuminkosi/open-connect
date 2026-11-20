using OpenConnect.Docs.Cloud;
using OpenConnect.Privacy.Rules;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class CloudInferenceOutputPrivacyTests
{
    private static CloudCredentialIdentity Identity() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Development", new HashSet<string> { "inference.chat" });

    [Fact]
    public async Task OutputPrivacyBlocksSensitiveOutputAndMissingDetector()
    {
        var identity = Identity();
        var requestId = Guid.NewGuid();
        var service = new CloudInferenceOutputPrivacyService(new RulesSecurityDetector());
        Assert.Equal("allowed", (await service.EvaluateAsync(identity, requestId, "Hello from the model.", default)).Code);
        Assert.Equal("privacy_blocked", (await service.EvaluateAsync(identity, requestId, "person@example.test", default)).Code);
        Assert.Equal("detector_unavailable", (await new CloudInferenceOutputPrivacyService(null).EvaluateAsync(identity, requestId, "safe", default)).Code);
        Assert.Equal("invalid_request", (await service.EvaluateAsync(identity, requestId, new string('x', 131073), default)).Code);
        Assert.Equal("invalid_request", (await service.EvaluateAsync(identity, Guid.Empty, "safe", default)).Code);
    }

    [Fact]
    public async Task OutputPrivacyPropagatesCancellation()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var service = new CloudInferenceOutputPrivacyService(new RulesSecurityDetector());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.EvaluateAsync(Identity(), Guid.NewGuid(), "safe", cancelled.Token));
    }
}
