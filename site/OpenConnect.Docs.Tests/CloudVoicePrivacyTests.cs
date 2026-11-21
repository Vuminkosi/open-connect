using OpenConnect.Docs.Cloud;
using OpenConnect.Privacy.Rules;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class CloudVoicePrivacyTests
{
    [Fact]
    public async Task InspectsAllWorkerFieldsAndCountsUnicodeScalars()
    {
        var identity = new CloudCredentialIdentity(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Development", new HashSet<string> { "voice.synthesize" });
        var service = new CloudVoicePrivacyService(new RulesSecurityDetector());
        var requestId = Guid.NewGuid();
        var input = new CloudVoicePrivacyRequest("Hello 🌍", "af_heart", "en-US", "wav");
        var allowed = await service.EvaluateAsync(identity, requestId, input, default);
        Assert.Equal("allowed", allowed.Code);
        Assert.Equal(7, allowed.Characters);
        Assert.Equal("privacy_blocked", (await service.EvaluateAsync(identity, requestId, input with { Text = "person@example.test" }, default)).Code);
        Assert.Equal("detector_unavailable", (await new CloudVoicePrivacyService(null).EvaluateAsync(identity, requestId, input, default)).Code);
        Assert.Equal("invalid_request", (await service.EvaluateAsync(identity, requestId, input with { Text = new string('x', 2001) }, default)).Code);
        Assert.Equal("invalid_request", (await service.EvaluateAsync(identity, requestId, input with { Text = "\ud800" }, default)).Code);
        Assert.Equal("invalid_request", (await service.EvaluateAsync(identity, requestId, input with { Format = "mp3" }, default)).Code);
    }
}
