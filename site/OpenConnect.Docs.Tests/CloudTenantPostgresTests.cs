using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using Npgsql;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class CloudPostgresFactAttribute : FactAttribute
{
    public CloudPostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")))
            Skip = "Set OPENCONNECT_CLOUD_TEST_DATABASE to a disposable PostgreSQL database.";
    }
}

public class CloudTenantPostgresTests
{
    [CloudPostgresFact]
    public async Task ConcurrentRemovalAndCreationHaveConsistentOutcome()
    {
        var connection = Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE");
        var normalOptions = new DbContextOptionsBuilder<CloudControlPlaneDbContext>().UseNpgsql(connection).Options;
        IDbContextFactory<CloudControlPlaneDbContext> normalFactory = new TestFactory(normalOptions);
        await using (var db = await normalFactory.CreateDbContextAsync()) await db.Database.MigrateAsync();
        var owner = new CloudSubject("test-issuer", Guid.NewGuid().ToString("N"));
        var normalStore = new EfCloudTenantStore(normalFactory, TimeProvider.System);
        var tenant = await normalStore.CreateTenantAsync(owner, "Concurrent test", CancellationToken.None);
        var gate = new MembershipReadGate();
        var gatedOptions = new DbContextOptionsBuilder<CloudControlPlaneDbContext>().UseNpgsql(connection).AddInterceptors(gate).Options;
        var gatedStore = new EfCloudTenantStore(new TestFactory(gatedOptions), TimeProvider.System);
        var creation = gatedStore.CreateApplicationAsync(owner, tenant.Id, "Concurrent application", "Development", CancellationToken.None);
        try
        {
            await gate.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await using var db = await normalFactory.CreateDbContextAsync();
            await db.Memberships.Where(x => x.TenantId == tenant.Id && x.SubjectId == owner.SubjectId && x.SubjectIssuer == owner.Issuer).ExecuteDeleteAsync();
        }
        finally
        {
            gate.Resume.TrySetResult();
        }

        CloudApplicationSummary? created = null;
        try { created = (await creation).Application; }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.SerializationFailure) { }
        var denied = await normalStore.CreateApplicationAsync(owner, tenant.Id, "After removal", "Development", CancellationToken.None);
        Assert.Null(denied.Application);
        await using var verify = await normalFactory.CreateDbContextAsync();
        var count = created is null ? 0 : 1;
        Assert.Equal(count, await verify.Applications.CountAsync(x => x.TenantId == tenant.Id));
        Assert.Equal(count, await verify.AuditEvents.CountAsync(x => x.TenantId == tenant.Id && x.Action == "application.created"));
    }

    [CloudPostgresFact]
    public async Task RemovedMembershipDeniesSubsequentApplicationCreation()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE"))
            .Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new TestFactory(options);
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.MigrateAsync();
        var store = new EfCloudTenantStore(factory, TimeProvider.System);
        var owner = new CloudSubject("test-issuer", Guid.NewGuid().ToString("N"));
        var tenant = await store.CreateTenantAsync(owner, "Transaction test", CancellationToken.None);
        var first = await store.CreateApplicationAsync(owner, tenant.Id, "Before removal", "Development", CancellationToken.None);
        Assert.NotNull(first.Application);

        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Memberships.Where(x => x.TenantId == tenant.Id && x.SubjectIssuer == owner.Issuer && x.SubjectId == owner.SubjectId)
                .ExecuteDeleteAsync();
        }
        var denied = await store.CreateApplicationAsync(owner, tenant.Id, "After removal", "Development", CancellationToken.None);
        Assert.Null(denied.Application);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(1, await verify.Applications.CountAsync(x => x.TenantId == tenant.Id));
        Assert.Equal(1, await verify.AuditEvents.CountAsync(x => x.TenantId == tenant.Id && x.Action == "application.created"));
    }

    private sealed class TestFactory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    {
        public CloudControlPlaneDbContext CreateDbContext() => new(options);
    }

    private sealed class MembershipReadGate : DbCommandInterceptor
    {
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM cloud.memberships", StringComparison.Ordinal))
            {
                ReadStarted.TrySetResult();
                await Resume.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            return result;
        }
    }
}
