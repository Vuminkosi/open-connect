using Microsoft.EntityFrameworkCore;
using OpenConnect.Navigator.Operational;
using OpenConnect.Navigator.Persistence.Postgres;
using OpenConnect.Persistence.Postgres;
using OpenConnect.Persistence.Postgres.Entities;
using Xunit;

namespace OpenConnect.Navigator.Persistence.Tests;

public sealed class OperationalPersistenceDeferralTests
{
    [Fact]
    public async Task OperationalReceipt_SavesWithParentSessionAndReadsBack_NullSummaryAndDuplicateReturnsFalse()
    {
        var options = new DbContextOptionsBuilder<OpenConnectDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new OpenConnectDbContext(options);
        var store = new PostgresDurableNavigatorStore(db);
        var tenant = Guid.NewGuid().ToString("D");
        var now = DateTimeOffset.UtcNow;
        var receipt = new OperationalReceipt($"{tenant}:job", OperationalRunStatus.Completed, 2, null, 1);

        Assert.True(await store.TrySaveReceiptAsync(tenant, "idempotency-key", receipt, now, CancellationToken.None));
        Assert.Equal(receipt, await store.GetReceiptAsync(tenant, "idempotency-key", CancellationToken.None));
        Assert.False(await store.TrySaveReceiptAsync(tenant, "idempotency-key", receipt, now, CancellationToken.None));
    }

    [Fact]
    public async Task IsLeaseCurrent_UsesExecutionTenantWithoutRequiringLeaseIdToEqualExecutionId()
    {
        var options = new DbContextOptionsBuilder<OpenConnectDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new OpenConnectDbContext(options);
        var tenant = Guid.NewGuid();
        var executionId = $"{tenant:D}:job";
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(1);
        db.Leases.Add(new LeaseEntity { TenantId = tenant, Name = executionId, Owner = "owner", ExpiresAt = expiresAt, Version = 7 });
        await db.SaveChangesAsync();
        var store = new PostgresDurableNavigatorStore(db);

        Assert.True(await store.IsLeaseCurrentAsync(new OperationalLease("lease-guid-not-execution-id", executionId, 7, expiresAt), DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.False(await store.IsLeaseCurrentAsync(new OperationalLease("bad", "not-a-tenant:job", 7, expiresAt), DateTimeOffset.UtcNow, CancellationToken.None));
    }

    [Fact]
    public async Task InvalidOperationalReceiptTenant_FailsClosedWithoutDatabaseAccess()
    {
        var options = new DbContextOptionsBuilder<OpenConnectDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new OpenConnectDbContext(options);
        var store = new PostgresDurableNavigatorStore(db);

        Assert.Null(await store.GetReceiptAsync("not-a-uuid", "key", CancellationToken.None));
        Assert.False(await store.TrySaveReceiptAsync("not-a-uuid", "key",
            new OperationalReceipt("execution", OperationalRunStatus.Completed, 0, null, 1),
            DateTimeOffset.UtcNow, CancellationToken.None));
    }
}
