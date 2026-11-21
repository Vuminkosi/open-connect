using OpenConnect.Docs.Cloud;
using OpenConnect.Privacy.Rules;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class CloudVoiceOutputPrivacyTests
{
    [Fact]
    public async Task ScreensRecognizedTranscriptAndFailsClosedWhenDetectorIsUnavailable()
    {
        var identity = new CloudCredentialIdentity(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Development", new HashSet<string> { "voice.synthesize" });
        var id = Guid.NewGuid();
        var service = new CloudVoiceOutputPrivacyService(new RulesSecurityDetector());
        Assert.Equal("allowed", (await service.EvaluateAsync(identity, id, "Hello there", default)).Code);
        Assert.Equal("privacy_blocked", (await service.EvaluateAsync(identity, id, "person@example.test", default)).Code);
        Assert.Equal("invalid_request", (await service.EvaluateAsync(identity, id, " ", default)).Code);
        Assert.Equal("invalid_request", (await service.EvaluateAsync(identity, id, new string('x', 8193), default)).Code);
        Assert.Equal("detector_unavailable", (await new CloudVoiceOutputPrivacyService(null).EvaluateAsync(identity, id, "Hello there", default)).Code);
    }
}
