using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Persistence;
using OpenConnect.Persistence.EFCore.Entities;
using OpenConnect.Persistence.EFCore.Stores;
using Xunit;

namespace OpenConnect.Persistence.EFCore.Tests;

public sealed class SqlServerLocalDbFactAttribute : FactAttribute
{
    public SqlServerLocalDbFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("OPENCONNECT_REQUIRE_SQLSERVER_LOCALDB_TESTS"), "true", StringComparison.OrdinalIgnoreCase))
            Skip = "NOT VERIFIED: explicit disposable Windows LocalDB fixture opt-in is required.";
    }
}

[Trait("Category", "PrivacyTokenMigrationSqlServer")]
public sealed class PrivacyTokenMigrationSqlServerTests
{
    [SqlServerLocalDbFact]
    public async Task Duplicate_upgrade_preserves_rows_and_old_filtered_index()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var db = fixture.Context();
        var previous = PreviousMigration(db);
        await db.GetService<IMigrator>().MigrateAsync(previous);
        var old = PrivacyTokenMigrationTests.Binding(1);
        var newer = PrivacyTokenMigrationTests.Binding(2);
        db.AddRange(old, newer);
        await db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<SqlException>(() => db.GetService<IMigrator>().MigrateAsync());
        Assert.Contains(error.Number, new[] { 1505, 2601, 2627 });

        db.ChangeTracker.Clear();
        var rows = await db.Set<PrivacyTokenEntity>().OrderBy(x => x.CreatedUtc).ToArrayAsync();
        Assert.Equal(new[] { old.Id, newer.Id }, rows.Select(x => x.Id));
        Assert.Equal(new byte[] { 1 }, rows[0].EncryptedValue);
        Assert.Equal(new byte[] { 2 }, rows[1].EncryptedValue);
        Assert.Equal(previous, (await db.Database.GetAppliedMigrationsAsync()).Last());
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sys.indexes WHERE object_id=OBJECT_ID(N'openconnect.PrivacyTokens') AND name=N'IX_PrivacyTokens_TenantScope_PrincipalOrWorkloadScope_TokenId_SessionId_TurnId' AND filter_definition IS NOT NULL";
        Assert.Equal(1, Convert.ToInt32(await command.ExecuteScalarAsync()));
    }

    [SqlServerLocalDbFact]
    public async Task Clean_upgrade_enforces_null_turn_uniqueness_and_down_preserves_rows()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var db = fixture.Context();
        var previous = PreviousMigration(db);
        await db.GetService<IMigrator>().MigrateAsync(previous);
        var row = PrivacyTokenMigrationTests.Binding(1);
        db.Add(row);
        await db.SaveChangesAsync();
        await db.GetService<IMigrator>().MigrateAsync();
        db.Add(PrivacyTokenMigrationTests.Binding(2));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        Assert.Equal(row.Id, (await db.Set<PrivacyTokenEntity>().SingleAsync()).Id);
        await db.GetService<IMigrator>().MigrateAsync(previous);
        Assert.Equal(row.Id, (await db.Set<PrivacyTokenEntity>().SingleAsync()).Id);
        db.Add(PrivacyTokenMigrationTests.Binding(2));
        await db.SaveChangesAsync();
        Assert.Equal(2, await db.Set<PrivacyTokenEntity>().CountAsync());
    }

    [SqlServerLocalDbFact]
    public async Task Case_insensitive_database_does_not_return_another_ordinal_principal_binding()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var db = fixture.Context();
        await db.GetService<IMigrator>().MigrateAsync();
        var row = PrivacyTokenMigrationTests.Binding(1);
        var tenant = new TenantId(Ulid.NewUlid());
        row.TenantScope = UlidGuidMapper.Scope(tenant.Value);
        db.Add(row);
        await db.SaveChangesAsync();
        var vault = new EfPrivacyTokenVault<HostMsContext>(db);
        var session = new SessionId(UlidGuidMapper.FromGuid(row.SessionId));
        Assert.NotNull(await vault.GetAsync(row.TokenId, tenant, row.PrincipalOrWorkloadScope, session));
        foreach (var (token, principal) in new[]
        {
            ("TOKEN", "principal"), ("token ", "principal"),
            ("token", "PRINCIPAL"), ("token", "principal ")
        })
        {
            Assert.Equal(1, await db.Set<PrivacyTokenEntity>().CountAsync(x => x.TokenId == token && x.PrincipalOrWorkloadScope == principal));
            Assert.Null(await vault.GetAsync(token, tenant, principal, session));
            await Assert.ThrowsAsync<InvalidOperationException>(() => vault.StoreAsync(
                new PrivacyTokenBinding(token, tenant, principal, session, "email", [2], new DateTimeOffset(row.ExpiresUtc).AddDays(1))));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var original = await vault.GetAsync(row.TokenId, tenant, row.PrincipalOrWorkloadScope, session);
            Assert.Equal(new byte[] { 1 }, original!.EncryptedValue);
            Assert.Equal(new DateTimeOffset(row.ExpiresUtc), original.ExpiresAt);
            Assert.Equal(1, await db.Set<PrivacyTokenEntity>().CountAsync());
        }
    }

    private static string PreviousMigration(HostMsContext db)
    {
        var migrations = db.Database.GetMigrations().ToArray();
        Assert.EndsWith("_PrivacyTokenScopeUniqueness", migrations[^1]);
        return migrations[^2];
    }

    private sealed class Fixture(string connectionString) : IAsyncDisposable
    {
        public HostMsContext Context() => new(new DbContextOptionsBuilder<HostMsContext>().UseSqlServer(connectionString).Options);

        public static async Task<Fixture> CreateAsync()
        {
            Assert.True(OperatingSystem.IsWindows(), "The explicitly required LocalDB fixture needs Windows.");
            var name = "oc_privacy_migration_" + Guid.NewGuid().ToString("N");
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
                // Identifier contains only the fixed prefix and a locally generated hex GUID.
                command.CommandText = $"CREATE DATABASE [{name}] COLLATE Latin1_General_100_CI_AS";
                await command.ExecuteNonQueryAsync();
            }
            connection.InitialCatalog = name;
            return new Fixture(connection.ConnectionString);
        }

        public async ValueTask DisposeAsync()
        {
            // The only reachable database is this fixture's freshly generated database.
            await using var db = Context();
            await db.Database.EnsureDeletedAsync();
        }
    }
}
