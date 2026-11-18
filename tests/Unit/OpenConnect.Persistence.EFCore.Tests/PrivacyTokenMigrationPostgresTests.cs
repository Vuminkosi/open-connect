using DotNet.Testcontainers.Images;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using OpenConnect.Persistence.EFCore.Entities;
using Testcontainers.PostgreSql;
using Xunit;

namespace OpenConnect.Persistence.EFCore.Tests;

[Trait("Category", "PrivacyTokenMigrationPostgres")]
public sealed class PrivacyTokenMigrationPostgresTests
{
    [PostgresDockerFact]
    public async Task Duplicate_upgrade_rolls_back_without_losing_bindings_or_old_index()
    {
        await using var postgres = Container();
        await postgres.StartAsync();
        await using var db = Context(postgres);
        var previous = PreviousMigration(db);
        await db.GetService<IMigrator>().MigrateAsync(previous);
        var old = PrivacyTokenMigrationTests.Binding(1);
        var newer = PrivacyTokenMigrationTests.Binding(2);
        db.AddRange(old, newer);
        await db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);

        db.ChangeTracker.Clear();
        var rows = await db.Set<PrivacyTokenEntity>().OrderBy(x => x.CreatedUtc).ToArrayAsync();
        Assert.Equal(new[] { old.Id, newer.Id }, rows.Select(x => x.Id));
        Assert.Equal(new byte[] { 1 }, rows[0].EncryptedValue);
        Assert.Equal(new byte[] { 2 }, rows[1].EncryptedValue);
        Assert.Equal(previous, (await db.Database.GetAppliedMigrationsAsync()).Last());
        var indexes = await IndexesAsync(db);
        Assert.Contains(indexes, sql => sql.Contains("TurnId", StringComparison.Ordinal));
        Assert.DoesNotContain(indexes, sql => sql.Contains("ScopeUpgrade", StringComparison.Ordinal));
    }

    [PostgresDockerFact]
    public async Task Clean_upgrade_rejects_duplicate_null_turn_and_down_preserves_binding()
    {
        await using var postgres = Container();
        await postgres.StartAsync();
        await using var db = Context(postgres);
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

    private static PostgreSqlContainer Container() => new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("privacy_token_upgrade").WithUsername("test").WithPassword("test")
        .WithImagePullPolicy(PullPolicy.Never).Build();

    private static HostPgContext Context(PostgreSqlContainer postgres) =>
        new(new DbContextOptionsBuilder<HostPgContext>().UseNpgsql(postgres.GetConnectionString()).Options);

    private static string PreviousMigration(HostPgContext db)
    {
        var migrations = db.Database.GetMigrations().ToArray();
        Assert.EndsWith("_PrivacyTokenScopeUniqueness", migrations[^1]);
        return migrations[^2];
    }

    private static async Task<string[]> IndexesAsync(HostPgContext db)
    {
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT indexdef FROM pg_indexes WHERE schemaname='openconnect' AND tablename='PrivacyTokens'";
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            values.Add(reader.GetString(0));
        return values.ToArray();
    }
}
