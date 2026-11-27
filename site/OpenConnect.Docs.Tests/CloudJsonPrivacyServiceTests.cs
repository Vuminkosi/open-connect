using System.Text.Json;
using OpenConnect.Docs.Cloud;
using OpenConnect.Privacy.Rules;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class CloudJsonPrivacyServiceTests
{
    [Theory]
    [InlineData("{\"input\":{\"text\":\"ordinary content\"}}", "allowed")]
    [InlineData("{\"input\":{\"text\":\"person@example.test\"}}", "privacy_blocked")]
    public async Task InspectsDecodedPayloadAndFailsClosedOnSensitiveContent(string json, string expected)
    {
        var identity = new CloudCredentialIdentity(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Development", new HashSet<string>());
        using var document = JsonDocument.Parse(json);
        var result = await new CloudJsonPrivacyService(new RulesSecurityDetector())
            .EvaluateWithEvidenceAsync(identity, Guid.NewGuid(), document.RootElement, CancellationToken.None);
        Assert.Equal(expected, result.Decision.Code);
        if (expected == "privacy_blocked")
            Assert.NotEmpty(result.Categories);
    }

    [Fact]
    public async Task MissingDetectorFailsClosed()
    {
        var identity = new CloudCredentialIdentity(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Development", new HashSet<string>());
        using var document = JsonDocument.Parse("{}");
        var result = await new CloudJsonPrivacyService(null)
            .EvaluateWithEvidenceAsync(identity, Guid.NewGuid(), document.RootElement, CancellationToken.None);
        Assert.Equal("detector_unavailable", result.Decision.Code);
    }
}
