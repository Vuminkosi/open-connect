using System.Text.Json;
using OpenConnect.Docs.Cloud;
using OpenConnect.Privacy.Rules;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class CloudAnalyticsPrivacyServiceTests
{
    [Theory]
    [InlineData("{\"input\":{\"feature\":\"weight\",\"values\":[1,2]}}", "allowed")]
    [InlineData("{\"input\":{\"lineage\":\"person@example.test\"}}", "privacy_blocked")]
    [InlineData("{\"input\":{\"lineage\":\"person\\u0040example.test\"}}", "privacy_blocked")]
    public async Task EvaluatesAnalyticsTextUsingThePrivacyBoundary(string json, string expected)
    {
        var identity = new CloudCredentialIdentity(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Development", new HashSet<string>());
        using var document = JsonDocument.Parse(json);
        var result = await new CloudAnalyticsPrivacyService(new RulesSecurityDetector())
            .EvaluateAsync(identity, Guid.NewGuid(), document.RootElement, CancellationToken.None);
        Assert.Equal(expected, result.Code);
        Assert.Equal(expected == "allowed", result.Allowed);
    }

    [Fact]
    public async Task MissingDetectorFailsClosed()
    {
        var identity = new CloudCredentialIdentity(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Development", new HashSet<string>());
        using var document = JsonDocument.Parse("{\"input\":{\"values\":[1]}}");
        var result = await new CloudAnalyticsPrivacyService(null)
            .EvaluateAsync(identity, Guid.NewGuid(), document.RootElement, CancellationToken.None);
        Assert.Equal("detector_unavailable", result.Code);
    }
}
