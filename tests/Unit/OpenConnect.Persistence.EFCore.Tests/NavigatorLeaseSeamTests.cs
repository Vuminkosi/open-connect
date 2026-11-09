// Behavior tests for the EF Core Navigator lease seam (TASK PERSIST-04-NAV-SEAM).
// Sqlite in-memory through the production ApplyOpenConnectModel path; no live database,
// no raw SQL, no model/migration changes. The seam is constructed directly with the
// DbContext (no DI registration edits, per contract).

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using OpenConnect.Persistence.EFCore.Context;
using OpenConnect.Persistence.EFCore.Navigator;
using Xunit;

namespace OpenConnect.Persistence.EFCore.Tests;

public sealed class NavigatorLeaseSeamTests
{
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);
    private static readonly DateTimeOffset T0 =
        new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero).AddTicks(1234567);

    [Fact]
    public async Task Acquire_new_lease_returns_token_one_with_truncated_expiry()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = Context(connection);
        await db.Database.EnsureCreatedAsync();
        var seam = new EfNavigatorLeaseSeam<RelationalPersistenceTests.ApplicationDbContext>(db);

        var lease = await seam.TryAcquireAsync(Execution("run-1"), "owner-a", Hour, T0);

        Assert.NotNull(lease);
        Assert.Equal(1, lease.FencingToken);
        Assert.False(string.IsNullOrWhiteSpace(lease.LeaseId));
        Assert.Equal(Truncate(T0 + Hour), lease.ExpiresAt);
        Assert.True(await seam.IsCurrentAsync(lease, T0));
    }

    [Fact]
    public async Task Acquire_live_lease_conflicts_and_original_stays_current()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = Context(connection);
        await db.Database.EnsureCreatedAsync();
        var seam = new EfNavigatorLeaseSeam<RelationalPersistenceTests.ApplicationDbContext>(db);
        var executionId = Execution("run-1");

        var first = await seam.TryAcquireAsync(executionId, "owner-a", Hour, T0);

        Assert.NotNull(first);
        Assert.Null(await seam.TryAcquireAsync(executionId, "owner-b", Hour, T0));
        Assert.True(await seam.IsCurrentAsync(first, T0));
        Assert.False(await seam.IsCurrentAsync(first with { FencingToken = first.FencingToken + 1 }, T0));
    }

    [Fact]
    public async Task Acquire_expired_lease_takeover_advances_token_and_keeps_row()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = Context(connection);
        await db.Database.EnsureCreatedAsync();
        var seam = new EfNavigatorLeaseSeam<RelationalPersistenceTests.ApplicationDbContext>(db);
        var executionId = Execution("run-1");

        var first = await seam.TryAcquireAsync(executionId, "owner-a", Hour, T0);
        var second = await seam.TryAcquireAsync(executionId, "owner-b", Hour, T0 + 2 * Hour);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(2, second.FencingToken);
        Assert.Equal(first.LeaseId, second.LeaseId); // Expired row retained and reused.
        Assert.False(await seam.IsCurrentAsync(first, T0 + 2 * Hour));
        Assert.True(await seam.IsCurrentAsync(second, T0 + 2 * Hour));
    }

    [Fact]
    public async Task Renew_current_advances_token_and_invalidates_old_token()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = Context(connection);
        await db.Database.EnsureCreatedAsync();
        var seam = new EfNavigatorLeaseSeam<RelationalPersistenceTests.ApplicationDbContext>(db);

        var first = await seam.TryAcquireAsync(Execution("run-1"), "owner-a", Hour, T0);
        Assert.NotNull(first);

        var renewed = await seam.RenewAsync(first, Hour, T0 + TimeSpan.FromMinutes(30));

        Assert.NotNull(renewed);
        Assert.Equal(first.LeaseId, renewed.LeaseId);
        Assert.Equal(first.FencingToken + 1, renewed.FencingToken);
        Assert.Equal(Truncate(T0 + TimeSpan.FromMinutes(30) + Hour), renewed.ExpiresAt);
        Assert.False(await seam.IsCurrentAsync(first, T0 + TimeSpan.FromMinutes(30)));
        Assert.True(await seam.IsCurrentAsync(renewed, T0 + TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public async Task Renew_stale_expired_or_unknown_returns_null()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = Context(connection);
        await db.Database.EnsureCreatedAsync();
        var seam = new EfNavigatorLeaseSeam<RelationalPersistenceTests.ApplicationDbContext>(db);
        var executionId = Execution("run-1");

        var first = await seam.TryAcquireAsync(executionId, "owner-a", Hour, T0);
        Assert.NotNull(first);
        var renewed = await seam.RenewAsync(first, Hour, T0 + TimeSpan.FromMinutes(30));
        Assert.NotNull(renewed);

        // Stale token.
        Assert.Null(await seam.RenewAsync(first, Hour, T0 + TimeSpan.FromMinutes(30)));
        // Expired lease.
        Assert.Null(await seam.RenewAsync(renewed, Hour, T0 + 2 * Hour));
        // Unknown execution.
        var unknown = new NavigatorLease(Guid.NewGuid().ToString("D"), Execution("nope"), 1, T0 + Hour);
        Assert.Null(await seam.RenewAsync(unknown, Hour, T0));
        Assert.False(await seam.IsCurrentAsync(unknown, T0));
    }

    [Fact]
    public async Task Release_expires_row_and_reacquire_advances_token()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = Context(connection);
        await db.Database.EnsureCreatedAsync();
        var seam = new EfNavigatorLeaseSeam<RelationalPersistenceTests.ApplicationDbContext>(db);
        var executionId = Execution("run-1");

        var first = await seam.TryAcquireAsync(executionId, "owner-a", Hour, T0);
        Assert.NotNull(first);
        var renewed = await seam.RenewAsync(first, Hour, T0 + TimeSpan.FromMinutes(30));
        Assert.NotNull(renewed);

        await seam.ReleaseAsync(renewed);
        Assert.False(await seam.IsCurrentAsync(renewed, T0 + TimeSpan.FromMinutes(30)));

        // Release is idempotent; unknown tokens are a no-op.
        await seam.ReleaseAsync(renewed);
        await seam.ReleaseAsync(renewed with { FencingToken = 999 });

        var reacquired = await seam.TryAcquireAsync(executionId, "owner-b", Hour, T0 + TimeSpan.FromMinutes(31));

        Assert.NotNull(reacquired);
        Assert.Equal(3, reacquired.FencingToken); // Row retained: 1 (acquire) + 1 (renew) + 1.
        Assert.Equal(first.LeaseId, reacquired.LeaseId);
        Assert.True(await seam.IsCurrentAsync(reacquired, T0 + TimeSpan.FromMinutes(31)));
    }

    [Theory]
    [InlineData("no-separator-here")]
    [InlineData(":empty-tenant")]
    [InlineData("not-a-uuid:run-1")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("")]
    public async Task Invalid_execution_ids_fail_closed(string executionId)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = Context(connection);
        await db.Database.EnsureCreatedAsync();
        var seam = new EfNavigatorLeaseSeam<RelationalPersistenceTests.ApplicationDbContext>(db);

        Assert.Null(await seam.TryAcquireAsync(executionId, "owner-a", Hour, T0));
        Assert.False(await seam.IsCurrentAsync(
            new NavigatorLease(Guid.NewGuid().ToString("D"), executionId, 1, T0 + Hour), T0));
        // Release of malformed ids is a no-op and must not throw.
        await seam.ReleaseAsync(
            new NavigatorLease(Guid.NewGuid().ToString("D"), executionId, 1, T0 + Hour));
    }

    [Fact]
    public async Task Invalid_durations_owners_and_lengths_fail_closed()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = Context(connection);
        await db.Database.EnsureCreatedAsync();
        var seam = new EfNavigatorLeaseSeam<RelationalPersistenceTests.ApplicationDbContext>(db);
        var executionId = Execution("run-1");

        Assert.Null(await seam.TryAcquireAsync(executionId, "owner-a", TimeSpan.Zero, T0));
        Assert.Null(await seam.TryAcquireAsync(executionId, "owner-a", TimeSpan.FromTicks(-1), T0));
        Assert.Null(await seam.TryAcquireAsync(executionId, "", Hour, T0));
        Assert.Null(await seam.TryAcquireAsync(executionId, "  ", Hour, T0));
        Assert.Null(await seam.TryAcquireAsync(executionId, new string('o', 257), Hour, T0));
        Assert.Null(await seam.TryAcquireAsync($"{Guid.NewGuid():D}:{new string('r', 300)}", "owner-a", Hour, T0));

        var first = await seam.TryAcquireAsync(executionId, "owner-a", Hour, T0);
        Assert.NotNull(first);
        Assert.Null(await seam.RenewAsync(first, TimeSpan.Zero, T0));
    }

    [Fact]
    public async Task Same_local_suffix_under_different_tenants_is_independent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = Context(connection);
        await db.Database.EnsureCreatedAsync();
        var seam = new EfNavigatorLeaseSeam<RelationalPersistenceTests.ApplicationDbContext>(db);

        var left = await seam.TryAcquireAsync($"{Guid.NewGuid():D}:run-1", "owner-a", Hour, T0);
        var right = await seam.TryAcquireAsync($"{Guid.NewGuid():D}:run-1", "owner-a", Hour, T0);

        Assert.NotNull(left);
        Assert.NotNull(right);
        Assert.Equal(1, left.FencingToken);
        Assert.Equal(1, right.FencingToken);
        Assert.True(await seam.IsCurrentAsync(left, T0));
        Assert.True(await seam.IsCurrentAsync(right, T0));
    }

    private static RelationalPersistenceTests.ApplicationDbContext Context(SqliteConnection connection) => new(
        new DbContextOptionsBuilder<RelationalPersistenceTests.ApplicationDbContext>()
            .UseSqlite(connection)
            .ReplaceService<IModelCacheKeyFactory, OpenConnectModelCacheKeyFactory>()
            .Options,
        prefix: true);

    private static string Execution(string local) => $"{Guid.NewGuid():D}:{local}";

    private static DateTimeOffset Truncate(DateTimeOffset value) =>
        new(value.UtcTicks - value.UtcTicks % 10, TimeSpan.Zero);
}
