using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class CloudHostPrivacyReportTests
{
    private static readonly CloudCredentialIdentity Identity = new(Guid.NewGuid(), Guid.Parse("00000000-0000-0000-0000-000000000001"),
        Guid.NewGuid(), "Development", new HashSet<string>());
    private const string Valid = """{"schemaVersion":"openconnect.privacy-evidence.v1","eventId":"11111111-1111-1111-1111-111111111111","tenantId":"00000000000000000000000001","destination":"Output","allowed":false,"action":"Block","categories":{"EMAIL":2}}""";

    [Fact]
    public void Migration_script_includes_append_only_guards_without_connecting_to_database()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused").Options;
        using var db = new CloudControlPlaneDbContext(options);
        var script = db.GetService<IMigrator>().GenerateScript("20261120163836_AddCloudPrivacyEvidence", "20261121235959_AddHostPrivacyEvidence");
        Assert.Contains("CREATE TABLE cloud.host_privacy_evidence", script);
        Assert.Contains("BEFORE UPDATE OR DELETE ON cloud.host_privacy_evidence", script);
        Assert.Contains("BEFORE TRUNCATE ON cloud.host_privacy_evidence", script);
        Assert.Contains("reject_privacy_evidence_mutation", script);
        var reasonMigration = db.GetService<IMigrator>().GenerateScript("20261121235959_AddHostPrivacyEvidence", "20261122000000_AddHostPrivacyReason");
        Assert.Contains("ADD COLUMN \"ReasonCode\"", reasonMigration);
        Assert.Contains("DEFAULT 'not_recorded'", reasonMigration);
        Assert.Contains("ck_host_privacy_reason_decision", reasonMigration);
        Assert.Contains("ck_host_privacy_reason_evidence", reasonMigration);
    }

    [Fact]
    public void Parses_content_free_evidence_only_for_authenticated_tenant()
    {
        using var json = JsonDocument.Parse(Valid);
        var result = CloudHostPrivacyReportParser.Parse(json.RootElement, Identity);
        Assert.Equal("Output", result.Destination.ToString());
        Assert.False(result.Allowed);
        Assert.Equal("{\"EMAIL\":2}", result.CategoryCounts);
        Assert.Equal("not_recorded", result.ReasonCode);
        Assert.Throws<ArgumentException>(() => CloudHostPrivacyReportParser.Parse(json.RootElement, Identity with { TenantId = Guid.NewGuid() }));
    }

    [Fact]
    public void ParsesAllowlistedInspectionFailureReason()
    {
        using var json = JsonDocument.Parse(Valid.Replace("\"allowed\":false", "\"allowed\":false,\"reasonCode\":\"inspection_failed\"", StringComparison.Ordinal));
        Assert.Equal("inspection_failed", CloudHostPrivacyReportParser.Parse(json.RootElement, Identity).ReasonCode);
    }

    [Fact]
    public void Rejects_review_action_claimed_as_allowed()
    {
        var body = Valid.Replace("\"allowed\":false", "\"allowed\":true", StringComparison.Ordinal)
            .Replace("\"action\":\"Block\"", "\"action\":\"Review\"", StringComparison.Ordinal);
        using var json = JsonDocument.Parse(body);
        Assert.Throws<ArgumentException>(() => CloudHostPrivacyReportParser.Parse(json.RootElement, Identity));
    }

    [Theory]
    [InlineData("not_recorded")]
    [InlineData("inspection_failed")]
    [InlineData("enforcement_unavailable")]
    [InlineData("sensitive_detected")]
    [InlineData("policy_blocked")]
    public void ParsesEachAllowlistedBlockedReason(string reason)
    {
        var body = Valid.Replace("\"allowed\":false", $"\"allowed\":false,\"reasonCode\":\"{reason}\"", StringComparison.Ordinal);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(reason, CloudHostPrivacyReportParser.Parse(json.RootElement, Identity).ReasonCode);
    }

    [Theory]
    [InlineData("\"categories\":{\"EMAIL\":2}", "\"categories\":{\"EMAIL\":2,\"EMAIL\":1}")]
    [InlineData("\"categories\":{\"EMAIL\":2}", "\"categories\":{\"EMAIL\":20000,\"PHONE\":1}")]
    [InlineData("\"categories\":{\"EMAIL\":2}", "\"categories\":{\"private-name\":1}")]
    [InlineData("\"allowed\":false", "\"allowed\":false,\"content\":\"sensitive\"")]
    [InlineData("\"action\":\"Block\"", "\"action\":\"3\"")]
    [InlineData("\"allowed\":false", "\"allowed\":true")]
    [InlineData("\"allowed\":false", "\"allowed\":false,\"reasonCode\":\"private diagnostic\"")]
    [InlineData("\"allowed\":false", "\"allowed\":false,\"reasonCode\":\"allowed\"")]
    [InlineData("\"categories\":{\"EMAIL\":2}", "\"reasonCode\":\"sensitive_detected\",\"categories\":{}")]
    public void Rejects_duplicate_excess_or_sensitive_fields(string original, string replacement)
    {
        using var json = JsonDocument.Parse(Valid.Replace(original, replacement, StringComparison.Ordinal));
        Assert.Throws<ArgumentException>(() => CloudHostPrivacyReportParser.Parse(json.RootElement, Identity));
    }
}
