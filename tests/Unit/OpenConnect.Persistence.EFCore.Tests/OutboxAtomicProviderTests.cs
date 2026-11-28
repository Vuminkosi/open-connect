using System.Data.Common;
using DotNet.Testcontainers.Images;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using OpenConnect.Abstractions.Persistence;
using OpenConnect.Persistence.EFCore.Entities;
using OpenConnect.Persistence.EFCore.Providers;
using Testcontainers.PostgreSql;
using Xunit;

namespace OpenConnect.Persistence.EFCore.Tests;

public sealed class OutboxAtomicProviderTests
{
    [PostgresDockerFact]
    [Trait("Category", "OutboxAtomicPostgres")]
    public Task PostgreSql_live_completion_and_retry() => PostgreSql(false);

    [PostgresDockerFact]
    [Trait("Category", "OutboxAtomicPostgres")]
    public Task PostgreSql_expiry_between_read_and_update_rejects_both_mutations() => PostgreSql(true);

    [SqlServerLocalDbFact]
    [Trait("Category", "OutboxAtomicSqlServer")]
    public Task SqlServer_live_completion_and_retry() => SqlServer(false);

    [SqlServerLocalDbFact]
    [Trait("Category", "OutboxAtomicSqlServer")]
    public Task SqlServer_expiry_between_read_and_update_rejects_both_mutations() => SqlServer(true);

    private static async Task PostgreSql(bool expire)
    {
        await using var postgres = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("outbox_atomic").WithUsername("test").WithPassword("test")
            .WithImagePullPolicy(PullPolicy.Never).Build();
        await postgres.StartAsync();
        var interceptor = new ExpireBeforeUpdateInterceptor(true);
        await using var db = new HostPgContext(new DbContextOptionsBuilder<HostPgContext>()
            .UseNpgsql(postgres.GetConnectionString()).AddInterceptors(interceptor).Options);
        await db.Database.MigrateAsync();
        await VerifyAsync(db, interceptor, expire);
    }

    private static async Task SqlServer(bool expire)
    {
        Assert.True(OperatingSystem.IsWindows(), "Explicit LocalDB tests require Windows and the preinstalled isolated instance.");
        var name = "oc_outbox_atomic_" + Guid.NewGuid().ToString("N");
        var connection = new SqlConnectionStringBuilder
        {
            DataSource = @"(localdb)\OpenConnectPrivacyMigration",
            InitialCatalog = "master",
            IntegratedSecurity = true,
            TrustServerCertificate = true,
            ConnectTimeout = 15
        };
        await using (var master = new SqlConnection(connection.ConnectionString))
        {
            await master.OpenAsync();
            await using var command = master.CreateCommand();
            // The identifier is exclusively a fixed prefix plus a fresh local hex GUID.
            command.CommandText = $"CREATE DATABASE [{name}]";
            await command.ExecuteNonQueryAsync();
        }
        connection.InitialCatalog = name;
        var interceptor = new ExpireBeforeUpdateInterceptor(false);
        await using var db = new HostMsContext(new DbContextOptionsBuilder<HostMsContext>()
            .UseSqlServer(connection.ConnectionString).AddInterceptors(interceptor).Options);
        try
        {
            await db.Database.MigrateAsync();
            await VerifyAsync(db, interceptor, expire);
        }
        finally
        {
            interceptor.Armed = false;
            // Only this test's freshly generated database is reachable here.
            await db.Database.EnsureDeletedAsync();
        }
    }

    private static async Task VerifyAsync(DbContext db, ExpireBeforeUpdateInterceptor interceptor, bool expire)
    {
        foreach (var retry in new[] { false, true })
        {
            var now = DateTime.UtcNow;
            var row = new OutboxEntity
            {
                Id = Guid.NewGuid(),
                TenantScope = "atomic-tenant",
                EventId = Guid.NewGuid().ToString(),
                Subject = "atomic.test",
                SanitizedPayload = "opaque-payload",
                Status = "pending",
                LeaseOwner = "atomic-owner",
                FencingVersion = 7,
                LeaseExpiresUtc = expire ? now.AddSeconds(5) : now.AddMinutes(5),
                CreatedUtc = now,
                NextAttemptUtc = now,
                Attempts = 1
            };
            db.Add(row);
            await db.SaveChangesAsync();
            var originalNextAttempt = await db.Set<OutboxEntity>().AsNoTracking()
                .Where(x => x.Id == row.Id).Select(x => x.NextAttemptUtc).SingleAsync();
            var claim = new OutboxClaim(row.Id.ToString(), row.TenantScope, row.FencingVersion,
                row.LeaseOwner, new DateTimeOffset(row.LeaseExpiresUtc.Value));
            interceptor.ExpiresUtc = row.LeaseExpiresUtc.Value;
            interceptor.Armed = expire;
            if (retry)
                await OutboxClaimOperations.RetryClaimAsync(db, claim, 2, CancellationToken.None);
            else
                Assert.Equal(!expire, await OutboxClaimOperations.CompleteClaimAsync(db, claim, CancellationToken.None));
            Assert.False(interceptor.Armed); // Expiry injection must actually reach the atomic UPDATE.
            db.ChangeTracker.Clear();
            var persisted = await db.Set<OutboxEntity>().AsNoTracking().SingleAsync(x => x.Id == row.Id);
            Assert.Equal(row.TenantScope, persisted.TenantScope);
            Assert.Equal(7, persisted.FencingVersion);
            Assert.Equal("opaque-payload", persisted.SanitizedPayload);
            if (expire)
            {
                Assert.Equal("pending", persisted.Status);
                Assert.Equal(1, persisted.Attempts);
                Assert.Equal("atomic-owner", persisted.LeaseOwner);
                Assert.Null(persisted.PublishedUtc);
                Assert.Equal(originalNextAttempt, persisted.NextAttemptUtc);
                Assert.True(persisted.LeaseExpiresUtc < DateTime.UtcNow);
            }
            else if (retry)
            {
                Assert.Equal("quarantined", persisted.Status);
                Assert.Equal(2, persisted.Attempts);
                Assert.Null(persisted.LeaseOwner);
                Assert.Null(persisted.LeaseExpiresUtc);
                Assert.Null(persisted.PublishedUtc);
                Assert.True(persisted.NextAttemptUtc > now);
            }
            else
            {
                Assert.Equal("delivered", persisted.Status);
                Assert.NotNull(persisted.PublishedUtc);
                Assert.Equal(1, persisted.Attempts);
            }
        }
        Assert.Equal(expire ? 2 : 0, interceptor.Injections);
    }

    private sealed class ExpireBeforeUpdateInterceptor(bool postgres) : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        public int Injections { get; private set; }
        public DateTime ExpiresUtc { get; set; }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Armed && command.CommandText.StartsWith("UPDATE ", StringComparison.Ordinal)
                && command.CommandText.Contains(postgres ? "clock_timestamp()" : "SYSUTCDATETIME()", StringComparison.Ordinal))
            {
                Armed = false;
                // Let the lease naturally expire after EF's fresh read but before its UPDATE.
                // No fixture row mutation: a timestamp captured before execution would wrongly succeed.
                var delay = ExpiresUtc - DateTime.UtcNow + TimeSpan.FromMilliseconds(200);
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, cancellationToken);
                Injections++;
            }
            return result;
        }
    }
}
