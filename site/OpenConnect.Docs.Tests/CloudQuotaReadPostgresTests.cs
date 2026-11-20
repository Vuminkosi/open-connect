using Microsoft.EntityFrameworkCore;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

[Collection("Cloud PostgreSQL")]
public sealed class CloudQuotaReadPostgresTests
{
    [CloudPostgresFact]
    public async Task QuotaReadsEnforceTenantIssuerAndBillingRolesAndPageResults()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new TestFactory(options);
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.MigrateAsync();
        var owner = new CloudSubject("issuer-a", Guid.NewGuid().ToString("N"));
        var billing = new CloudSubject("issuer-a", Guid.NewGuid().ToString("N"));
        var administrator = new CloudSubject("issuer-a", Guid.NewGuid().ToString("N"));
        var developer = new CloudSubject("issuer-a", Guid.NewGuid().ToString("N"));
        var store = new EfCloudTenantStore(factory, TimeProvider.System);
        var tenant = await store.CreateTenantAsync(owner, "Quota read", CancellationToken.None);
        var app = (await store.CreateApplicationAsync(owner, tenant.Id, "App", "Development", CancellationToken.None)).Application!;
        var other = await store.CreateTenantAsync(owner, "Other quota read", CancellationToken.None);
        var otherApp = (await store.CreateApplicationAsync(owner, other.Id, "App", "Development", CancellationToken.None)).Application!;
        var capability = "test." + Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Capabilities.Add(new CloudCapabilityDefinition { Id = capability, Name = "Test", Description = "Test", ContractVersion = "1", BillingUnit = "request", PlatformAvailability = "available", Dependencies = "[]", Modalities = "[]", AllowedRegions = "[]" });
            db.Memberships.AddRange(
                new CloudMembership { TenantId = tenant.Id, SubjectIssuer = billing.Issuer, SubjectId = billing.SubjectId, Role = "BillingReader", CreatedAt = now },
                new CloudMembership { TenantId = tenant.Id, SubjectIssuer = administrator.Issuer, SubjectId = administrator.SubjectId, Role = "Administrator", CreatedAt = now },
                new CloudMembership { TenantId = tenant.Id, SubjectIssuer = developer.Issuer, SubjectId = developer.SubjectId, Role = "Developer", CreatedAt = now });
            for (var i = 0; i < 3; i++)
                db.QuotaBuckets.Add(new CloudQuotaBucket { Id = Guid.NewGuid(), TenantId = tenant.Id, ApplicationId = app.Id, CapabilityId = capability,
                    Unit = "request", PeriodStart = now.AddHours(i), PeriodEnd = now.AddHours(i + 1), Limit = 10, Consumed = 2, Reserved = 3 });
            db.QuotaBuckets.Add(new CloudQuotaBucket { Id = Guid.NewGuid(), TenantId = other.Id, ApplicationId = otherApp.Id, CapabilityId = capability,
                Unit = "request", PeriodStart = now, PeriodEnd = now.AddHours(1), Limit = 99 });
            await db.SaveChangesAsync();
        }
        var service = new CloudQuotaQueryService(new EfCloudQuotaReader(factory));
        var first = await service.GetTenantAsync(billing, tenant.Id, 0, 2, CancellationToken.None);
        Assert.NotNull(first);
        Assert.Equal(2, first.Quotas.Count);
        Assert.Equal(2, first.NextOffset);
        Assert.All(first.Quotas, x => { Assert.Equal(app.Id, x.ApplicationId); Assert.Equal(10, x.Limit); Assert.Equal(2, x.Consumed); Assert.Equal(3, x.Reserved); });
        var second = await service.GetTenantAsync(billing, tenant.Id, first.NextOffset!.Value, 2, CancellationToken.None);
        Assert.Single(second!.Quotas);
        Assert.Null(second.NextOffset);
        Assert.DoesNotContain(second.Quotas[0].Id, first.Quotas.Select(x => x.Id));
        Assert.Null(await service.GetTenantAsync(developer, tenant.Id, 0, 2, CancellationToken.None));
        Assert.NotNull(await service.GetTenantAsync(owner, tenant.Id, 0, 2, CancellationToken.None));
        Assert.NotNull(await service.GetTenantAsync(administrator, tenant.Id, 0, 2, CancellationToken.None));
        Assert.Null(await service.GetTenantAsync(new CloudSubject("issuer-b", billing.SubjectId), tenant.Id, 0, 2, CancellationToken.None));
        Assert.Null(await service.GetTenantAsync(billing, other.Id, 0, 2, CancellationToken.None));
    }

    private sealed class TestFactory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    {
        public CloudControlPlaneDbContext CreateDbContext() => new(options);
    }
}
