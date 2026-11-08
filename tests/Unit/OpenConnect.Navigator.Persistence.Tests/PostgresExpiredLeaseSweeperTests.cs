using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OpenConnect.Navigator.Persistence.Postgres;
using OpenConnect.Persistence.Postgres;
using OpenConnect.Persistence.Postgres.Entities;
using Testcontainers.PostgreSql;
using Xunit;
using Xunit.Abstractions;

namespace OpenConnect.Navigator.Persistence.Tests;

[Trait("Category", "PostgresDocker")]
public sealed class PostgresExpiredLeaseSweeperTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Sweep_deletes_only_expiries_older_than_retention_on_postgres()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:16-alpine").WithDatabase("expired_lease_sweep_test")
            .WithUsername("test").WithPassword("test").Build();
        Exception? startupError = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try { await postgres.StartAsync(); startupError = null; break; }
            catch (Exception e) { startupError = e; }
        }
        if (startupError is not null)
        {
            output.WriteLine($"SKIP PostgresDocker: Docker/Testcontainers could not start postgres:16-alpine after 2 attempts: {startupError.Message}");
            return;
        }

        var connectionString = postgres.GetConnectionString();
        Assert.Contains("expired_lease_sweep_test", connectionString, StringComparison.OrdinalIgnoreCase);
        await using (var migration = CreateDb(connectionString)) await migration.GetService<IMigrator>().MigrateAsync();

        var tenant = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using (var seed = CreateDb(connectionString))
        {
            seed.Leases.AddRange(
                new LeaseEntity { TenantId = tenant, Name = "old", Owner = "owner", ExpiresAt = now.AddDays(-8), Version = 5 },
                new LeaseEntity { TenantId = tenant, Name = "recent", Owner = "owner", ExpiresAt = now.AddDays(-1), Version = 6 },
                new LeaseEntity { TenantId = tenant, Name = "live", Owner = "owner", ExpiresAt = now.AddMinutes(10), Version = 7 });
            await seed.SaveChangesAsync();
        }

        await using (var db = CreateDb(connectionString))
        {
            var deleted = await new PostgresExpiredLeaseSweeper(db).SweepAsync(TimeSpan.FromDays(7));
            Assert.Equal(1, deleted);
        }

        await using var verify = CreateDb(connectionString);
        Assert.Equal(new[] { "live", "recent" }, await verify.Leases.OrderBy(x => x.Name).Select(x => x.Name).ToArrayAsync());
        Assert.Equal(new long[] { 6, 7 }, await verify.Leases.OrderBy(x => x.Version).Select(x => x.Version).ToArrayAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Sweep_rejects_nonpositive_retention_without_database_io(int days)
    {
        await using var db = new OpenConnectDbContext(new DbContextOptionsBuilder<OpenConnectDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new PostgresExpiredLeaseSweeper(db).SweepAsync(TimeSpan.FromDays(days)));
    }

    private static OpenConnectDbContext CreateDb(string connectionString) =>
        new(new DbContextOptionsBuilder<OpenConnectDbContext>().UseNpgsql(connectionString).Options);
}
