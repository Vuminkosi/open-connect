using OpenConnect.Docs.Cloud;
using OpenConnect.Privacy.Rules;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class CloudLiveUIPrivacyTests
{
    private static CloudCredentialIdentity Identity() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Development", new HashSet<string> { "inference.liveui" });

    [Fact]
    public async Task ChecksIntentRouteLabelsAndValuesSplitAcrossFields()
    {
        var service = new CloudLiveUIPrivacyService(new RulesSecurityDetector());
        var identity = Identity(); var requestId = Guid.NewGuid();
        Assert.Equal("allowed", (await service.EvaluateAsync(identity, requestId, ["v1", "show tasks", "route.root", "task.tab", "activate", "Tasks"], default)).Code);
        Assert.Equal("privacy_blocked", (await service.EvaluateAsync(identity, requestId, ["v1", "show tasks", "route.root", "task.tab", "activate", "person@example.test"], default)).Code);
        Assert.Equal("privacy_blocked", (await service.EvaluateAsync(identity, requestId, ["v1", "person@", "example.test"], default)).Code);
        Assert.Equal("detector_unavailable", (await new CloudLiveUIPrivacyService(null).EvaluateAsync(identity, requestId, ["v1", "safe", "route"], default)).Code);
        Assert.Equal("invalid_request", (await service.EvaluateAsync(identity, requestId, ["v1", "safe"], default)).Code);
        Assert.Equal("invalid_request", (await service.EvaluateAsync(identity, requestId, ["v1", new string('x', 16001), "route"], default)).Code);
    }

    [Fact]
    public async Task CancellationIsPropagated()
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var service = new CloudLiveUIPrivacyService(new RulesSecurityDetector());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.EvaluateAsync(Identity(), Guid.NewGuid(), ["v1", "safe", "route"], cancelled.Token));
    }
}
