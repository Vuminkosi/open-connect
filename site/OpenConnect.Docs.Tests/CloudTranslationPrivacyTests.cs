using OpenConnect.Docs.Cloud;
using OpenConnect.Privacy.Rules;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class CloudTranslationPrivacyTests
{
    [Fact]
    public async Task InputScreensWorkerFieldsAndCountsUnicodeScalars()
    {
        var identity = Identity();
        var id = Guid.NewGuid();
        var service = new CloudTranslationPrivacyService(new RulesSecurityDetector());
        var input = new CloudTranslationPrivacyInput("Hello \U0001f30d", "eng_Latn", "fra_Latn");
        var allowed = await service.InspectInputAsync(identity, id, input, default);
        Assert.Equal("allowed", allowed.Code);
        Assert.Equal(7, allowed.Characters);
        Assert.Equal("privacy_blocked", (await service.InspectInputAsync(identity, id, input with { Text = "person@example.test" }, default)).Code);
        Assert.Equal("detector_unavailable", (await new CloudTranslationPrivacyService(null).InspectInputAsync(identity, id, input, default)).Code);
        Assert.Equal("invalid_request", (await service.InspectInputAsync(identity, id, input with { Text = new string('x', 5001) }, default)).Code);
        Assert.Equal("invalid_request", (await service.InspectInputAsync(identity, id, input with { Text = "\ud800" }, default)).Code);
        Assert.Equal("invalid_request", (await service.InspectInputAsync(identity, id, input with { SourceLang = "en" }, default)).Code);
        Assert.Equal("invalid_request", (await service.InspectInputAsync(identity, id, input with { TargetLang = input.SourceLang }, default)).Code);
    }

    [Fact]
    public async Task OutputScreensTranslatedTextAndFailsClosed()
    {
        var identity = Identity();
        var id = Guid.NewGuid();
        var service = new CloudTranslationPrivacyService(new RulesSecurityDetector());
        Assert.Equal("allowed", (await service.InspectOutputAsync(identity, id, "Bonjour", default)).Code);
        Assert.Equal("privacy_blocked", (await service.InspectOutputAsync(identity, id, "person@example.test", default)).Code);
        Assert.Equal("detector_unavailable", (await new CloudTranslationPrivacyService(null).InspectOutputAsync(identity, id, "Bonjour", default)).Code);
        Assert.Equal("invalid_request", (await service.InspectOutputAsync(identity, id, new string('x', 20001), default)).Code);
        Assert.Equal("invalid_request", (await service.InspectOutputAsync(identity, id, "\ud800", default)).Code);
        Assert.Equal("invalid_request", (await service.InspectOutputAsync(identity, Guid.Empty, "Bonjour", default)).Code);
    }

    private static CloudCredentialIdentity Identity() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Development",
        new HashSet<string> { "translation.translate" });
}
