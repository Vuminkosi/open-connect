using Microsoft.EntityFrameworkCore;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

[Collection("Cloud PostgreSQL")]
public sealed class CloudClassificationPrivacyEvidencePostgresTests
{
    [CloudPostgresFact]
    public async Task EvidencePersistsOnlyBoundedCategoriesAndRemainsAppendOnly()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new Factory(options);
        await using (var db = await factory.CreateDbContextAsync())
            await db.Database.MigrateAsync();
        var owner = new CloudSubject("test-issuer", Guid.NewGuid().ToString("N"));
        var tenants = new EfCloudTenantStore(factory, TimeProvider.System);
        var tenant = await tenants.CreateTenantAsync(owner, "Privacy evidence", default);
        var app = (await tenants.CreateApplicationAsync(owner, tenant.Id, "App", "Development", default)).Application!;
        var identity = new CloudCredentialIdentity(Guid.NewGuid(), tenant.Id, app.Id, "Development", new HashSet<string>());
        var request = Guid.NewGuid();
        var store = new EfCloudPrivacyEvidenceStore(factory, TimeProvider.System);
        await store.AppendAsync(identity, request, "privacy_blocked", new string('a', 32), [new("EMAIL", 2)], default);
        await Assert.ThrowsAsync<ArgumentException>(() => store.AppendAsync(identity, Guid.NewGuid(), "allowed", new string('b', 32), [new("EMAIL", 1)], default));
        await using var check = await factory.CreateDbContextAsync();
        var row = await check.PrivacyEvidence.SingleAsync(x => x.TenantId == tenant.Id && x.RequestId == request);
        Assert.Equal(new CloudPrivacyCategoryCount("EMAIL", 2), Assert.Single(CloudPrivacyCategories.Decode(row.CategoryCounts, row.Outcome)!));
        Assert.DoesNotContain("@", row.CategoryCounts!);
        await Assert.ThrowsAnyAsync<Exception>(() => check.PrivacyEvidence.Where(x => x.Id == row.Id).ExecuteDeleteAsync());
    }

    private sealed class Factory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    {
        public CloudControlPlaneDbContext CreateDbContext() => new(options);
    }
}
