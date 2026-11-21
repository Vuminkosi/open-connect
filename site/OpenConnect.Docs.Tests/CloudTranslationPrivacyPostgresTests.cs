using Microsoft.EntityFrameworkCore;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

[Collection("Cloud PostgreSQL")]
public sealed class CloudTranslationPrivacyPostgresTests
{
    [CloudPostgresFact]
    public async Task LatestEvidenceBindsTenantRequestFingerprintAndCharacterCount()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new TestFactory(options);
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.MigrateAsync();
        var owner = new CloudSubject("https://issuer.example", Guid.NewGuid().ToString("N"));
        var tenants = new EfCloudTenantStore(factory, TimeProvider.System);
        var tenant = await tenants.CreateTenantAsync(owner, "Translation privacy", default);
        var app = (await tenants.CreateApplicationAsync(owner, tenant.Id, "Translation app", "Development", default)).Application!;
        var identity = new CloudCredentialIdentity(Guid.NewGuid(), tenant.Id, app.Id, "Development", new HashSet<string> { "translation.translate" });
        var store = new EfCloudTranslationPrivacyEvidenceStore(factory, TimeProvider.System);
        var requestId = Guid.NewGuid(); var fingerprint = new byte[32]; var trace = new string('a', 32);
        await store.AppendAsync(identity, requestId, "key", fingerprint, 7, "allowed", trace, default);
        await using (var db = await factory.CreateDbContextAsync())
        {
            Assert.True(await EfCloudTranslationPrivacyAdmission.IsAllowedAsync(db, tenant.Id, app.Id, requestId, "key", fingerprint, 7, default));
            Assert.False(await EfCloudTranslationPrivacyAdmission.IsAllowedAsync(db, tenant.Id, app.Id, requestId, "key", fingerprint, 8, default));
            Assert.False(await EfCloudTranslationPrivacyAdmission.IsAllowedAsync(db, tenant.Id, app.Id, requestId, "key", Enumerable.Repeat((byte)1, 32).ToArray(), 7, default));
            Assert.False(await EfCloudTranslationPrivacyAdmission.IsAllowedAsync(db, Guid.NewGuid(), app.Id, requestId, "key", fingerprint, 7, default));
            Assert.False(await EfCloudTranslationPrivacyAdmission.IsAllowedAsync(db, tenant.Id, Guid.NewGuid(), requestId, "key", fingerprint, 7, default));
            Assert.False(await EfCloudTranslationPrivacyAdmission.IsAllowedAsync(db, tenant.Id, app.Id, Guid.NewGuid(), "key", fingerprint, 7, default));
            Assert.False(await EfCloudTranslationPrivacyAdmission.IsAllowedAsync(db, tenant.Id, app.Id, requestId, "other", fingerprint, 7, default));
        }
        await store.AppendAsync(identity, requestId, "key", fingerprint, 7, "privacy_blocked", trace, default);
        await using (var db = await factory.CreateDbContextAsync())
        {
            Assert.False(await EfCloudTranslationPrivacyAdmission.IsAllowedAsync(db, tenant.Id, app.Id, requestId, "key", fingerprint, 7, default));
            await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM cloud.translation_privacy_evidence WHERE \"RequestId\" = {requestId}"));
            await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cloud.translation_privacy_evidence SET \"Outcome\" = 'allowed' WHERE \"RequestId\" = {requestId}"));
        }
    }

    private sealed class TestFactory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    {
        public CloudControlPlaneDbContext CreateDbContext() => new(options);
    }
}
