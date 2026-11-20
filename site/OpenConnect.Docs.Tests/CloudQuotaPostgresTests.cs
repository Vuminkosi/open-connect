using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

[Collection("Cloud PostgreSQL")]
public sealed class CloudQuotaPostgresTests
{
    [CloudPostgresFact]
    public async Task ReservationsAreAtomicAndRetriesDoNotDoubleCharge()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new TestFactory(options);
        await using (var db = await factory.CreateDbContextAsync())
        {
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("20261120130640_AddInvitationRevocation");
        }
        var owner = new CloudSubject("test-issuer", Guid.NewGuid().ToString("N"));
        var tenants = new EfCloudTenantStore(factory, TimeProvider.System);
        var tenant = await tenants.CreateTenantAsync(owner, "Quota test", CancellationToken.None);
        var app = (await tenants.CreateApplicationAsync(owner, tenant.Id, "App", "Development", CancellationToken.None)).Application!;
        var capability = "test." + Guid.NewGuid().ToString("N");
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Capabilities.Add(new CloudCapabilityDefinition { Id = capability, Name = "Test", Description = "Test", ContractVersion = "1", BillingUnit = "request", PlatformAvailability = "available", Dependencies = "[]", Modalities = "[]", AllowedRegions = "[]" });
            await db.SaveChangesAsync();
            await db.Database.MigrateAsync();
        }
        var now = DateTimeOffset.UtcNow;
        var expiry = now.AddMinutes(5);
        var bucketId = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.QuotaBuckets.Add(new CloudQuotaBucket { Id = bucketId, TenantId = tenant.Id, ApplicationId = app.Id, CapabilityId = capability,
                Unit = "request", PeriodStart = now.AddHours(-1), PeriodEnd = now.AddHours(1), Limit = 10 });
            await db.SaveChangesAsync();
        }
        var otherTenant = await tenants.CreateTenantAsync(owner, "Other quota tenant", CancellationToken.None);
        var otherApp = (await tenants.CreateApplicationAsync(owner, otherTenant.Id, "Other app", "Development", CancellationToken.None)).Application!;
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.QuotaReservations.Add(new CloudQuotaReservation { Id = Guid.NewGuid(), BucketId = bucketId, TenantId = otherTenant.Id,
                ApplicationId = otherApp.Id, RequestId = Guid.NewGuid(), Amount = 1, ExpiresAt = expiry });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        var service = new CloudQuotaService(new EfCloudQuotaStore(factory), TimeProvider.System);
        CloudQuotaRequest Request(Guid id, long amount = 1) => new(tenant.Id, app.Id, capability, "request", id, amount, expiry);
        var attempts = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => service.ReserveAsync(Request(Guid.NewGuid()), CancellationToken.None)));
        var accepted = attempts.Where(x => x.Status == "reserved").ToArray();
        Assert.Equal(10, accepted.Length);
        Assert.Equal(90, attempts.Count(x => x.Status == "quota_exceeded"));
        Assert.All(accepted, x => Assert.NotNull(x.ReservationId));
        var id = accepted[0].ReservationId!.Value;
        Assert.Equal("not_found", (await service.FinalizeAsync(Guid.NewGuid(), app.Id, id, 0, CancellationToken.None)).Status);
        Assert.Equal("overage_requires_reconciliation", (await service.FinalizeAsync(tenant.Id, app.Id, id, 2, CancellationToken.None)).Status);
        var finalized = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => service.FinalizeAsync(tenant.Id, app.Id, id, 0, CancellationToken.None)));
        Assert.Single(finalized, x => x.Status == "finalized");
        Assert.Equal(7, finalized.Count(x => x.Status == "already_finalized"));
        Assert.Equal("idempotency_conflict", (await service.FinalizeAsync(tenant.Id, app.Id, id, 1, CancellationToken.None)).Status);
        var repeatedRequest = Request(Guid.NewGuid());
        var duplicates = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => service.ReserveAsync(repeatedRequest, CancellationToken.None)));
        Assert.Single(duplicates, x => x.Status == "reserved");
        Assert.Equal(11, duplicates.Count(x => x.Status == "duplicate"));
        Assert.Equal("idempotency_conflict", (await service.ReserveAsync(repeatedRequest with { Amount = 2 }, CancellationToken.None)).Status);
        var chargeId = accepted[1].ReservationId!.Value;
        Assert.Equal("finalized", (await service.FinalizeAsync(tenant.Id, app.Id, chargeId, 1, CancellationToken.None)).Status);
        Assert.Equal("already_finalized", (await service.FinalizeAsync(tenant.Id, app.Id, chargeId, 1, CancellationToken.None)).Status);
        var afterExpiry = new CloudQuotaService(new EfCloudQuotaStore(factory), new FixedTimeProvider(expiry.AddSeconds(1)));
        Assert.Equal("quota_exceeded", (await afterExpiry.ReserveAsync(Request(Guid.NewGuid()) with { ExpiresAt = expiry.AddMinutes(5) }, CancellationToken.None)).Status);
        await using var verify = await factory.CreateDbContextAsync();
        var bucket = await verify.QuotaBuckets.AsNoTracking().SingleAsync(x => x.Id == bucketId);
        Assert.Equal(9, bucket.Reserved);
        Assert.Equal(1, bucket.Consumed);
        Assert.Equal(11, await verify.QuotaReservations.CountAsync(x => x.BucketId == bucketId));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TestFactory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    {
        public CloudControlPlaneDbContext CreateDbContext() => new(options);
    }
}
