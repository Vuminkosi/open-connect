using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class CloudKnowledgeEmbeddingMigrationTests
{
    [Theory]
    [InlineData("analytics.analyze")]
    [InlineData("knowledge.embed")]
    public void ExpansionRequestQuotasUseExplicitUnit(string capability)
    {
        Assert.True(CloudQuotaUnits.Supports(capability, "not configured", "request"));
        Assert.False(CloudQuotaUnits.Supports(capability, "request", "token"));
    }

    [Fact]
    public void MigrationGeneratesSeparateAppendOnlyDirectionScopedEvidence()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused").Options;
        using var db = new CloudControlPlaneDbContext(options);
        var script = db.GetService<IMigrator>().GenerateScript("20261123000600_AddCapabilityAvailabilityRevisions",
            "20261124010000_AddKnowledgeEmbeddingPrivacyEvidence", MigrationsSqlGenerationOptions.Idempotent);
        Assert.Contains("CREATE TABLE cloud.knowledge_embedding_privacy_evidence", script, StringComparison.Ordinal);
        Assert.Contains("\"Direction\" IN ('request', 'response')", script, StringComparison.Ordinal);
        Assert.Contains("knowledge_embedding_privacy_evidence_append_only", script, StringComparison.Ordinal);
        Assert.Contains("knowledge_embedding_privacy_evidence_no_truncate", script, StringComparison.Ordinal);
        Assert.Contains("__EFMigrationsHistory", script, StringComparison.Ordinal);
        var deploymentScript = db.GetService<IMigrator>().GenerateScript("20261124010000_AddKnowledgeEmbeddingPrivacyEvidence",
            "20261124010100_AllowWorkerExpansionDeploymentClaims", MigrationsSqlGenerationOptions.Idempotent);
        Assert.Contains("guard_deployment_concurrency", deploymentScript, StringComparison.Ordinal);
        Assert.Contains("'knowledge.embed'", deploymentScript, StringComparison.Ordinal);
        Assert.Contains("'analytics.analyze'", deploymentScript, StringComparison.Ordinal);
        Assert.Contains("LOCK TABLE cloud.execution_claims, cloud.worker_deployments", deploymentScript, StringComparison.Ordinal);
        var recoveryScript = db.GetService<IMigrator>().GenerateScript("20261124010100_AllowWorkerExpansionDeploymentClaims",
            "20261124010200_RecoverUnconsumedWorkerExpansionClaims", MigrationsSqlGenerationOptions.Idempotent);
        Assert.Contains("validate_execution_abandonment", recoveryScript, StringComparison.Ordinal);
        Assert.Contains("'analytics.analyze', 'knowledge.embed'", recoveryScript, StringComparison.Ordinal);
        Assert.Contains("execution_consumptions", recoveryScript, StringComparison.Ordinal);
        var usageScript = db.GetService<IMigrator>().GenerateScript("20261124010200_RecoverUnconsumedWorkerExpansionClaims",
            "20261124010300_AddKnowledgeEmbeddingUsage", MigrationsSqlGenerationOptions.Idempotent);
        Assert.Contains("CREATE TABLE cloud.knowledge_embedding_usage", usageScript, StringComparison.Ordinal);
        Assert.Contains("\"InputCharacters\" BETWEEN 1 AND 65536", usageScript, StringComparison.Ordinal);
        Assert.Contains("cloud_knowledge_embedding_usage_append_only", usageScript, StringComparison.Ordinal);
        Assert.Contains("cloud.validate_knowledge_embedding_usage", usageScript, StringComparison.Ordinal);
        Assert.Contains("matching consumed completion", usageScript, StringComparison.Ordinal);
        Assert.Contains("matching response privacy evidence", usageScript, StringComparison.Ordinal);
    }
}
