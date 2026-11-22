using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OpenConnect.Docs.Cloud;
using OpenConnect.Privacy.Rules;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class CloudAnalyticsPrivacyTests
{
    [Fact]
    public void MigrationScriptIncludesBoundedUsageAndDirectionSpecificEvidence()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused").Options;
        using var db = new CloudControlPlaneDbContext(options);
        var script = db.GetService<IMigrator>().GenerateScript("20261122000000_AddHostPrivacyReason", "20261122000002_AddAnalyticsComputeUsage");
        Assert.Contains("CREATE TABLE cloud.analytics_privacy_evidence", script, StringComparison.Ordinal);
        Assert.Contains("\"Direction\" IN ('request', 'response')", script, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE cloud.analytics_compute_usage", script, StringComparison.Ordinal);
        Assert.Contains("BETWEEN 1 AND 120000", script, StringComparison.Ordinal);
        Assert.Contains("cloud_analytics_compute_usage_append_only", script, StringComparison.Ordinal);
        Assert.Contains("cloud.validate_analytics_compute_usage", script, StringComparison.Ordinal);
        Assert.Contains("matching consumed completion", script, StringComparison.Ordinal);
        Assert.Contains("matching response privacy evidence", script, StringComparison.Ordinal);
        var idempotent = db.GetService<IMigrator>().GenerateScript("20261122000000_AddHostPrivacyReason",
            "20261122000002_AddAnalyticsComputeUsage", MigrationsSqlGenerationOptions.Idempotent);
        Assert.Contains("__EFMigrationsHistory", idempotent, StringComparison.Ordinal);
        Assert.Contains("cloud_analytics_compute_usage_no_truncate", idempotent, StringComparison.Ordinal);
    }
    [Theory]
    [InlineData("{\"input\":{\"feature\":\"weight\",\"values\":[1,2]}}", "allowed")]
    [InlineData("{\"input\":{\"lineage\":\"person@example.test\"}}", "privacy_blocked")]
    [InlineData("{\"input\":{\"full_name\":{\"value\":\"Jane Doe\"}}}", "privacy_blocked")]
    [InlineData("{\"input\":{\"lineage\":\"person\\u0040example.test\"}}", "privacy_blocked")]
    public async Task InspectsDecodedAndContextualFields(string json, string expected)
    {
        var service = new CloudAnalyticsPrivacyService(new RulesSecurityDetector());
        var identity = new CloudCredentialIdentity(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Development", new HashSet<string>());
        using var document = JsonDocument.Parse(json);
        var result = await service.EvaluateAsync(identity, Guid.NewGuid(), document.RootElement, CancellationToken.None);
        Assert.Equal(expected, result.Code);
        Assert.Equal(expected == "allowed", result.Allowed);
    }

    [Fact]
    public async Task MissingDetectorFailsClosed()
    {
        var identity = new CloudCredentialIdentity(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Development", new HashSet<string>());
        using var document = JsonDocument.Parse("{\"input\":{\"values\":[1]}}");
        var result = await new CloudAnalyticsPrivacyService(null).EvaluateAsync(identity, Guid.NewGuid(), document.RootElement, CancellationToken.None);
        Assert.Equal("detector_unavailable", result.Code);
    }

    [Fact]
    public async Task SupportedTenThousandSampleInputCanBeInspected()
    {
        var identity = new CloudCredentialIdentity(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Development", new HashSet<string>());
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { input = new { feature = "weight", values = Enumerable.Repeat(1, 10000) } }));
        var result = await new CloudAnalyticsPrivacyService(new RulesSecurityDetector()).EvaluateAsync(identity, Guid.NewGuid(), document.RootElement, CancellationToken.None);
        Assert.Equal("allowed", result.Code);
    }
}
