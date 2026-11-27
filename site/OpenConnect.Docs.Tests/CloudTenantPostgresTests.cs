using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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

[Collection("Cloud PostgreSQL")]
public class CloudTenantPostgresTests
{
    [CloudPostgresFact]
    public async Task ConcurrentOwnerDemotionsCannotRemoveBothOwners()
    {
        var connection = Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE");
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>().UseNpgsql(connection).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new TestFactory(options);
        await using (var db = await factory.CreateDbContextAsync())
            await db.Database.MigrateAsync();
        var first = new CloudSubject("test-issuer", Guid.NewGuid().ToString("N"));
        var second = new CloudSubject("test-issuer", Guid.NewGuid().ToString("N"));
        var store = new EfCloudTenantStore(factory, TimeProvider.System);
        var tenant = await store.CreateTenantAsync(first, "Owner concurrency", CancellationToken.None);
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Memberships.Add(new CloudMembership
            {
                TenantId = tenant.Id,
                SubjectIssuer = second.Issuer,
                SubjectId = second.SubjectId,
                Role = "Owner",
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }
        var gate = new OwnerCountGate();
        var gatedOptions = new DbContextOptionsBuilder<CloudControlPlaneDbContext>().UseNpgsql(connection).AddInterceptors(gate).Options;
        var gatedStore = new EfCloudTenantStore(new TestFactory(gatedOptions), TimeProvider.System);
        var changes = await Task.WhenAll(
            gatedStore.ChangeMemberAsync(first, tenant.Id, second, "Developer", 1, CancellationToken.None),
            gatedStore.ChangeMemberAsync(second, tenant.Id, first, "Developer", 1, CancellationToken.None));
        Assert.Single(changes, x => x.Status == "succeeded");
        Assert.Single(changes, x => x.Status == "conflict");
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(1, await verify.Memberships.CountAsync(x => x.TenantId == tenant.Id && x.Role == "Owner"));
        Assert.Equal(1, await verify.AuditEvents.CountAsync(x => x.TenantId == tenant.Id && x.Action == "membership.role_changed.Owner.Developer"));
        Assert.Equal(1, await verify.AuditEvents.CountAsync(x => x.TenantId == tenant.Id && x.Action == "membership.change" && x.Outcome == "conflict"));
    }

    [CloudPostgresFact]
    public async Task MembershipChangesEnforceRoleRevisionAndLastOwner()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>().UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new TestFactory(options);
        await using (var db = await factory.CreateDbContextAsync())
            await db.Database.MigrateAsync();
        var owner = new CloudSubject("test-issuer", Guid.NewGuid().ToString("N"));
        var developer = new CloudSubject("test-issuer", Guid.NewGuid().ToString("N"));
        var store = new EfCloudTenantStore(factory, TimeProvider.System);
        var tenant = await store.CreateTenantAsync(owner, "Membership changes", CancellationToken.None);
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Memberships.Add(new CloudMembership
            {
                TenantId = tenant.Id,
                SubjectIssuer = developer.Issuer,
                SubjectId = developer.SubjectId,
                Role = "Developer",
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }
        var outsider = new CloudSubject("test-issuer", Guid.NewGuid().ToString("N"));
        Assert.Equal("not_found", (await store.ChangeMemberAsync(outsider, tenant.Id, owner, "Developer", 1, CancellationToken.None)).Status);
        Assert.Equal("forbidden", (await store.ChangeMemberAsync(developer, tenant.Id, owner, "Developer", 1, CancellationToken.None)).Status);
        Assert.Equal("last_owner", (await store.ChangeMemberAsync(owner, tenant.Id, owner, null, 1, CancellationToken.None)).Status);
        Assert.Equal("succeeded", (await store.ChangeMemberAsync(owner, tenant.Id, developer, "Administrator", 1, CancellationToken.None)).Status);
        Assert.Equal("conflict", (await store.ChangeMemberAsync(owner, tenant.Id, developer, null, 1, CancellationToken.None)).Status);
        Assert.Equal("succeeded", (await store.ChangeMemberAsync(owner, tenant.Id, developer, null, 2, CancellationToken.None)).Status);
        Assert.Null(await store.GetMembersAsync(developer, tenant.Id, CancellationToken.None));
        await using var verify = await factory.CreateDbContextAsync();
        Assert.False(await verify.Memberships.AnyAsync(x => x.TenantId == tenant.Id && x.SubjectId == developer.SubjectId));
        Assert.True(await verify.AuditEvents.AnyAsync(x => x.TenantId == null && x.SubjectId == outsider.SubjectId &&
            x.Action == "membership.change" && x.Outcome == "not_found" && x.ResourceId == tenant.Id.ToString("D")));
        var history = await verify.MembershipRevisions.SingleAsync(x => x.TenantId == tenant.Id);
        Assert.Equal(3, history.LastRevision);
        var failures = await verify.AuditEvents.Where(x => x.TenantId == tenant.Id && x.Action == "membership.change")
            .OrderBy(x => x.OccurredAt).ToListAsync();
        Assert.Equal(new[] { "forbidden", "last_owner", "conflict" }, failures.Select(x => x.Outcome));
        Assert.Equal(developer.SubjectId, failures[0].SubjectId);
        Assert.Equal("Developer", failures[0].ActorRole);
        Assert.All(failures, audit => Assert.DoesNotContain(developer.SubjectId, audit.ResourceId ?? ""));
    }

    [CloudPostgresFact]
    public async Task MemberDirectoryRequiresPrivilegedMembershipAndIsTenantScoped()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new TestFactory(options);
        await using (var db = await factory.CreateDbContextAsync())
            await db.Database.MigrateAsync();
        var store = new EfCloudTenantStore(factory, TimeProvider.System);
        var owner = new CloudSubject("test-issuer", Guid.NewGuid().ToString("N"));
        var developer = new CloudSubject("test-issuer", Guid.NewGuid().ToString("N"));
        var outsider = new CloudSubject("test-issuer", Guid.NewGuid().ToString("N"));
        var tenant = await store.CreateTenantAsync(owner, "Directory test", CancellationToken.None);
        var otherTenant = await store.CreateTenantAsync(outsider, "Other tenant", CancellationToken.None);
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Memberships.Add(new CloudMembership
            {
                TenantId = tenant.Id,
                SubjectIssuer = developer.Issuer,
                SubjectId = developer.SubjectId,
                Role = "Developer",
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }
        var members = await store.GetMembersAsync(owner, tenant.Id, CancellationToken.None);
        Assert.NotNull(members);
        Assert.Equal(2, members.Count);
        Assert.DoesNotContain(members, x => x.SubjectId == outsider.SubjectId);
        Assert.Null(await store.GetMembersAsync(developer, tenant.Id, CancellationToken.None));
        Assert.Null(await store.GetMembersAsync(outsider, tenant.Id, CancellationToken.None));
        Assert.Null(await store.GetMembersAsync(owner, otherTenant.Id, CancellationToken.None));
    }

    [CloudPostgresFact]
    public async Task ConcurrentRemovalAndCreationHaveConsistentOutcome()
    {
        var connection = Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE");
        var normalOptions = new DbContextOptionsBuilder<CloudControlPlaneDbContext>().UseNpgsql(connection).Options;
        IDbContextFactory<CloudControlPlaneDbContext> normalFactory = new TestFactory(normalOptions);
        await using (var db = await normalFactory.CreateDbContextAsync())
            await db.Database.MigrateAsync();
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
        try
        { created = (await creation).Application; }
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
        await using (var db = await factory.CreateDbContextAsync())
            await db.Database.MigrateAsync();
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
        Assert.False(denied.Forbidden);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(1, await verify.Applications.CountAsync(x => x.TenantId == tenant.Id));
        Assert.Equal(1, await verify.AuditEvents.CountAsync(x => x.TenantId == tenant.Id && x.Action == "application.created"));
        Assert.True(await verify.AuditEvents.AnyAsync(x => x.TenantId == null && x.SubjectId == owner.SubjectId &&
            x.Action == "application.create" && x.Outcome == "not_found" && x.ResourceId == tenant.Id.ToString("D")));
    }

    [CloudPostgresFact]
    public async Task CreateApplicationDistinguishesNonMemberFromForbiddenMemberAndAuditsDenials()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE"))
            .Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new TestFactory(options);
        await using (var db = await factory.CreateDbContextAsync())
            await db.Database.MigrateAsync();
        var store = new EfCloudTenantStore(factory, TimeProvider.System);
        var owner = new CloudSubject("test-issuer", Guid.NewGuid().ToString("N"));
        var developer = new CloudSubject("test-issuer", Guid.NewGuid().ToString("N"));
        var outsider = new CloudSubject("test-issuer", Guid.NewGuid().ToString("N"));
        var tenant = await store.CreateTenantAsync(owner, "App authz test", CancellationToken.None);
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Memberships.Add(new CloudMembership
            {
                TenantId = tenant.Id,
                SubjectIssuer = developer.Issuer,
                SubjectId = developer.SubjectId,
                Role = "Developer",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var forbidden = await store.CreateApplicationAsync(developer, tenant.Id, "Dev app", "Development", CancellationToken.None);
        Assert.Null(forbidden.Application);
        Assert.True(forbidden.Forbidden);

        var notFound = await store.CreateApplicationAsync(outsider, tenant.Id, "Outsider app", "Development", CancellationToken.None);
        Assert.Null(notFound.Application);
        Assert.False(notFound.Forbidden);

        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(0, await verify.Applications.CountAsync(x => x.TenantId == tenant.Id));
        Assert.True(await verify.AuditEvents.AnyAsync(x => x.TenantId == tenant.Id && x.SubjectId == developer.SubjectId &&
            x.ActorRole == "Developer" && x.Action == "application.create" && x.Outcome == "forbidden"));
        Assert.True(await verify.AuditEvents.AnyAsync(x => x.TenantId == null && x.SubjectId == outsider.SubjectId &&
            x.ActorRole == "AuthenticatedUser" && x.Action == "application.create" && x.Outcome == "not_found"));
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

    private sealed class OwnerCountGate : DbCommandInterceptor
    {
        private int _count;
        private readonly TaskCompletionSource _both = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("count(", StringComparison.OrdinalIgnoreCase) && command.CommandText.Contains("cloud.memberships", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref _count) == 2)
                    _both.TrySetResult();
                await _both.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            return result;
        }
    }
}
