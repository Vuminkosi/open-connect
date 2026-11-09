// Cross-process fencing tests for the EF Core Navigator lease seam
// (TASK SEAM-FENCING-TEST-R2).
//
// Mirrors the legacy PostgresCrossProcessFencingTests pattern (Testcontainers
// postgres:16-alpine, independent DbContext per host, bounded WaitAsync only)
// but targets EfNavigatorLeaseSeam through the production ApplyOpenConnectModel
// path (OpenConnectDbContext). No test-only model: the lease row is the
// production LeaseEntity mapping; no raw SQL; no migrations.
//
// Determinism note: expiry is simulated by passing explicit future `now`
// values (logical clock), never by Task.Delay. The only waits are bounded
// Testcontainers startup and Task.WhenAll.WaitAsync joins. Each host uses its
// own DbContext (own pooled connection) to prove the fence is enforced by
// persisted state, not by in-memory tracking.
//
// Image policy: postgres:16-alpine must already exist locally. If Docker or
// the image is unavailable the test skips (returns) like the legacy suite;
// it must never trigger a network pull in an offline lane.

using Microsoft.EntityFrameworkCore;
using OpenConnect.Persistence.EFCore.Context;
using OpenConnect.Persistence.EFCore.Navigator;
using Testcontainers.PostgreSql;
using Xunit;
using Xunit.Abstractions;

namespace OpenConnect.Persistence.EFCore.Tests;

[Trait("Category", "PostgresDocker")]
public sealed class NavigatorLeaseSeamFencingTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);
    private static readonly DateTimeOffset T0 =
        new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero).AddTicks(1234567);

    [Fact]
    public async Task Concurrent_acquire_from_independent_contexts_elects_single_holder_with_token_one()
    {
        await using var postgres = await StartPostgresAsync("navigator_seam_fencing_race");
        if (postgres is null)
            return;
        var connectionString = postgres.GetConnectionString();
        await using (var init = CreateDb(connectionString))
            await init.Database.EnsureCreatedAsync();

        var executionId = Execution("race");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<NavigatorLease?> AcquireAsHostAsync(string owner)
        {
            await using var db = CreateDb(connectionString);
            var seam = new EfNavigatorLeaseSeam<OpenConnectDbContext>(db);
            await gate.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return await seam.TryAcquireAsync(executionId, owner, Hour, T0);
        }

        var hostA = AcquireAsHostAsync("host-a");
        var hostB = AcquireAsHostAsync("host-b");
        gate.TrySetResult();
        var results = await Task.WhenAll(hostA, hostB).WaitAsync(TimeSpan.FromSeconds(60));

        // Exactly one winner: token 1. The loser fails closed with null
        // (unique-index race loser -> DbUpdateException -> null).
        Assert.Equal(1, results.Count(lease => lease is not null));
        Assert.Equal(1, results.Count(lease => lease is null));
        var winner = Assert.IsType<NavigatorLease>(results.Single(lease => lease is not null));
        Assert.Equal(1, winner.FencingToken);

        // The fence is visible from a third independent context.
        await using var witness = CreateDb(connectionString);
        Assert.True(await new EfNavigatorLeaseSeam<OpenConnectDbContext>(witness).IsCurrentAsync(winner, T0));
    }

    [Fact]
    public async Task Takeover_after_expiry_advances_token_and_stale_lease_is_denied()
    {
        await using var postgres = await StartPostgresAsync("navigator_seam_fencing_takeover");
        if (postgres is null)
            return;
        var connectionString = postgres.GetConnectionString();
        await using (var init = CreateDb(connectionString))
            await init.Database.EnsureCreatedAsync();

        var executionId = Execution("takeover");

        NavigatorLease first;
        await using (var dbA = CreateDb(connectionString))
            first = Assert.IsType<NavigatorLease>(
                await new EfNavigatorLeaseSeam<OpenConnectDbContext>(dbA)
                    .TryAcquireAsync(executionId, "host-a", Hour, T0));
        Assert.Equal(1, first.FencingToken);

        // Live lease from an independent host conflicts (stale denial while live).
        await using (var dbB = CreateDb(connectionString))
            Assert.Null(await new EfNavigatorLeaseSeam<OpenConnectDbContext>(dbB)
                .TryAcquireAsync(executionId, "host-b", Hour, T0));

        // Logical clock past expiry: takeover advances the fencing token by exactly one.
        var takeoverNow = T0 + 2 * Hour;
        NavigatorLease second;
        await using (var dbB = CreateDb(connectionString))
            second = Assert.IsType<NavigatorLease>(
                await new EfNavigatorLeaseSeam<OpenConnectDbContext>(dbB)
                    .TryAcquireAsync(executionId, "host-b", Hour, takeoverNow));
        Assert.Equal(2, second.FencingToken);
        Assert.Equal(first.LeaseId, second.LeaseId); // Expired row retained and reused.

        // Stale lease is denied: not current, and renew with the old token fails closed.
        await using (var staleDb = CreateDb(connectionString))
        {
            var staleSeam = new EfNavigatorLeaseSeam<OpenConnectDbContext>(staleDb);
            Assert.False(await staleSeam.IsCurrentAsync(first, takeoverNow));
            Assert.True(await staleSeam.IsCurrentAsync(second, takeoverNow));
            Assert.Null(await staleSeam.RenewAsync(first, Hour, takeoverNow));
        }
    }

    [Fact]
    public async Task Release_retains_row_and_next_acquire_advances_token_one_two_three()
    {
        await using var postgres = await StartPostgresAsync("navigator_seam_fencing_release");
        if (postgres is null)
            return;
        var connectionString = postgres.GetConnectionString();
        await using (var init = CreateDb(connectionString))
            await init.Database.EnsureCreatedAsync();

        var executionId = Execution("release");

        NavigatorLease first;
        await using (var db = CreateDb(connectionString))
            first = Assert.IsType<NavigatorLease>(
                await new EfNavigatorLeaseSeam<OpenConnectDbContext>(db)
                    .TryAcquireAsync(executionId, "host-a", Hour, T0));
        Assert.Equal(1, first.FencingToken);

        NavigatorLease renewed;
        var renewNow = T0 + TimeSpan.FromMinutes(30);
        await using (var db = CreateDb(connectionString))
            renewed = Assert.IsType<NavigatorLease>(
                await new EfNavigatorLeaseSeam<OpenConnectDbContext>(db).RenewAsync(first, Hour, renewNow));
        Assert.Equal(2, renewed.FencingToken);
        Assert.Equal(first.LeaseId, renewed.LeaseId);

        // Release from an independent context; the row is retained (MinValue expiry),
        // so the next acquire advances the token instead of restarting at 1.
        await using (var db = CreateDb(connectionString))
            await new EfNavigatorLeaseSeam<OpenConnectDbContext>(db).ReleaseAsync(renewed);

        await using (var db = CreateDb(connectionString))
            Assert.False(await new EfNavigatorLeaseSeam<OpenConnectDbContext>(db)
                .IsCurrentAsync(renewed, renewNow));

        var reacquireNow = T0 + TimeSpan.FromMinutes(31);
        NavigatorLease reacquired;
        await using (var db = CreateDb(connectionString))
            reacquired = Assert.IsType<NavigatorLease>(
                await new EfNavigatorLeaseSeam<OpenConnectDbContext>(db)
                    .TryAcquireAsync(executionId, "host-b", Hour, reacquireNow));
        Assert.Equal(3, reacquired.FencingToken); // 1 (acquire) + 1 (renew) + 1 (reacquire).
        Assert.Equal(first.LeaseId, reacquired.LeaseId);

        await using (var db = CreateDb(connectionString))
            Assert.True(await new EfNavigatorLeaseSeam<OpenConnectDbContext>(db)
                .IsCurrentAsync(reacquired, reacquireNow));
    }

    private async Task<PostgreSqlContainer?> StartPostgresAsync(string database)
    {
        var postgres = new PostgreSqlBuilder("postgres:16-alpine").WithDatabase(database)
            .WithUsername("test").WithPassword("test").Build();
        Exception? startupError = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                await postgres.StartAsync();
                startupError = null;
                break;
            }
            catch (Exception exception)
            {
                startupError = exception;
            }
        }

        if (startupError is null)
            return postgres;
        output.WriteLine(
            $"SKIP PostgresDocker: Docker/Testcontainers could not start postgres:16-alpine " +
            $"after 2 attempts: {startupError.Message}");
        await postgres.DisposeAsync();
        return null;
    }

    private static OpenConnectDbContext CreateDb(string connectionString) =>
        new(new DbContextOptionsBuilder<OpenConnectDbContext>().UseNpgsql(connectionString).Options);

    private static string Execution(string local) => $"{Guid.NewGuid():D}:{local}";
}
