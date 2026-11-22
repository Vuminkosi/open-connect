using Microsoft.EntityFrameworkCore;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

[Collection("Cloud PostgreSQL")]
public sealed class CloudKnowledgeEmbeddingPrivacyEvidencePostgresTests
{
    [CloudPostgresFact]
    public async Task LatestDecisionIsBoundToTenantApplicationAndFingerprint()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new Factory(options);
        await using (var db = await factory.CreateDbContextAsync())
            await db.Database.MigrateAsync();
        var owner = new CloudSubject("test-issuer", Guid.NewGuid().ToString("N"));
        var tenants = new EfCloudTenantStore(factory, TimeProvider.System);
        var tenant = await tenants.CreateTenantAsync(owner, "Embedding privacy", default);
        var app = (await tenants.CreateApplicationAsync(owner, tenant.Id, "App", "Development", default)).Application!;
        var identity = new CloudCredentialIdentity(Guid.NewGuid(), tenant.Id, app.Id, "Development", new HashSet<string>());
        var request = Guid.NewGuid();
        var fingerprint = new byte[32];
        var store = new EfCloudKnowledgeEmbeddingPrivacyEvidenceStore(factory, TimeProvider.System);
        await store.AppendAsync(identity, request, "active", fingerprint, "request", "allowed", new string('a', 32), [], 3, default);
        await using var check = await factory.CreateDbContextAsync();
        Assert.True(await EfCloudKnowledgeEmbeddingPrivacyAdmission.IsAllowedAsync(check, tenant.Id, app.Id, request, "active", fingerprint, default));
        Assert.False(await EfCloudKnowledgeEmbeddingPrivacyAdmission.IsAllowedAsync(check, Guid.NewGuid(), app.Id, request, "active", fingerprint, default));
        Assert.False(await EfCloudKnowledgeEmbeddingPrivacyAdmission.IsAllowedAsync(check, tenant.Id, app.Id, request, "other", fingerprint, default));
        await store.AppendAsync(identity, request, "active", fingerprint, "request", "privacy_blocked", new string('b', 32), [new("EMAIL", 1)], 3, default);
        Assert.False(await EfCloudKnowledgeEmbeddingPrivacyAdmission.IsAllowedAsync(check, tenant.Id, app.Id, request, "active", fingerprint, default));
        await Assert.ThrowsAnyAsync<Exception>(() => check.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM cloud.knowledge_embedding_privacy_evidence WHERE \"RequestId\" = {request}"));
    }

    private sealed class Factory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    {
        public CloudControlPlaneDbContext CreateDbContext() => new(options);
    }
}
