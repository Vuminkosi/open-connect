using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using OpenConnect.Persistence.EFCore.Entities;
using Xunit;

namespace OpenConnect.Persistence.EFCore.Tests;

public sealed class PrivacyTokenMigrationTests
{
    public static IEnumerable<object[]> SqlitePaths => Enumerable.Range(0, 6).Select(i => new object[] { i });

    private static Migration SqliteMigration(int path) => path switch
    {
        0 => new global::OpenConnect.Samples.Blazor.Migrations.PrivacyTokenScopeUniqueness(),
        1 => new global::OpenConnect.Samples.McpMicroservices.ServiceA.Migrations.PrivacyTokenScopeUniqueness(),
        2 => new global::OpenConnect.Samples.McpMicroservices.ServiceB.Migrations.PrivacyTokenScopeUniqueness(),
        3 => new global::OpenConnect.Samples.Minimal.Migrations.PrivacyTokenScopeUniqueness(),
        4 => new global::OpenConnect.Samples.Sqlite.Migrations.PrivacyTokenScopeUniqueness(),
        5 => new global::OpenConnect.Samples.Persistence.Sqlite.Migrations.PrivacyTokenScopeUniqueness(),
        _ => throw new ArgumentOutOfRangeException(nameof(path))
    };

    [Theory]
    [MemberData(nameof(SqlitePaths))]
    public async Task Duplicate_upgrade_fails_without_changing_rows_or_old_index(int path)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();
        var migration = SqliteMigration(path);
        await ExecuteAsync(db, migration.DownOperations);
        var older = Binding(1);
        var newer = Binding(2);
        db.AddRange(older, newer);
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(db, migration.UpOperations));

        db.ChangeTracker.Clear();
        var rows = await db.Set<PrivacyTokenEntity>().OrderBy(x => x.CreatedUtc).ToArrayAsync();
        Assert.Equal(new[] { older.Id, newer.Id }, rows.Select(x => x.Id));
        Assert.Equal(new byte[] { 1 }, rows[0].EncryptedValue);
        Assert.Equal(new byte[] { 2 }, rows[1].EncryptedValue);
        Assert.Equal(older.ExpiresUtc, rows[0].ExpiresUtc);
        Assert.Equal(newer.ExpiresUtc, rows[1].ExpiresUtc);
        Assert.True(await IndexExistsAsync(connection, ((CreateIndexOperation)migration.DownOperations[0]).Name));
        Assert.False(await IndexExistsAsync(connection, ((CreateIndexOperation)migration.UpOperations[0]).Name));
    }

    [Theory]
    [MemberData(nameof(SqlitePaths))]
    public async Task Clean_upgrade_enforces_null_turn_uniqueness_and_down_preserves_rows(int path)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();
        var migration = SqliteMigration(path);
        await ExecuteAsync(db, migration.DownOperations);
        var row = Binding(1);
        db.Add(row);
        await db.SaveChangesAsync();
        await ExecuteAsync(db, migration.UpOperations);
        db.Add(Binding(2));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        Assert.Equal(row.Id, (await db.Set<PrivacyTokenEntity>().SingleAsync()).Id);
        await ExecuteAsync(db, migration.DownOperations);
        Assert.Equal(row.Id, (await db.Set<PrivacyTokenEntity>().SingleAsync()).Id);
        db.Add(Binding(2));
        await db.SaveChangesAsync();
        Assert.Equal(2, await db.Set<PrivacyTokenEntity>().CountAsync());
    }

    [Fact]
    public void Provider_migrations_validate_new_index_before_removing_old_without_row_mutations()
    {
        foreach (var migration in new Migration[] { new MigrationsPg.PrivacyTokenScopeUniqueness(), new MigrationsMs.PrivacyTokenScopeUniqueness() })
        {
            foreach (var operations in new[] { migration.UpOperations, migration.DownOperations })
            {
                Assert.IsType<CreateIndexOperation>(operations[0]);
                Assert.True(((CreateIndexOperation)operations[0]).IsUnique);
                Assert.IsType<DropIndexOperation>(operations[1]);
                Assert.DoesNotContain(operations, operation => operation is SqlOperation or DeleteDataOperation or UpdateDataOperation);
                if (operations.Count == 3)
                {
                    var rename = Assert.IsType<RenameIndexOperation>(operations[2]);
                    Assert.Equal(((CreateIndexOperation)operations[0]).Name, rename.Name);
                    Assert.Equal(((DropIndexOperation)operations[1]).Name, rename.NewName);
                }
            }
        }
    }

    private static RelationalPersistenceTests.ApplicationDbContext CreateDb(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<RelationalPersistenceTests.ApplicationDbContext>().UseSqlite(connection)
            .ReplaceService<IModelCacheKeyFactory, Context.OpenConnectModelCacheKeyFactory>().Options, true);

    internal static PrivacyTokenEntity Binding(byte value) => new()
    {
        Id = Guid.NewGuid(),
        TenantScope = "tenant",
        PrincipalOrWorkloadScope = "principal",
        TokenId = "token",
        SessionId = Guid.Parse("00000000-0000-0000-0000-000000000001"),
        TurnId = null,
        EntityType = "email",
        EncryptedValue = [value],
        KeyProviderId = "test",
        KeyVersion = "1",
        BindingDigest = "test",
        CreatedUtc = new DateTime(2030, 1, value, 0, 0, 0, DateTimeKind.Utc),
        ExpiresUtc = new DateTime(2031, 1, value, 0, 0, 0, DateTimeKind.Utc)
    };

    private static async Task ExecuteAsync(DbContext db, IReadOnlyList<MigrationOperation> operations)
    {
        var commands = db.GetService<IMigrationsSqlGenerator>().Generate(operations, db.GetService<IDesignTimeModel>().Model);
        await using var transaction = await db.Database.BeginTransactionAsync();
        foreach (var command in commands)
            await db.Database.ExecuteSqlRawAsync(command.CommandText);
        await transaction.CommitAsync();
    }

    private static async Task<bool> IndexExistsAsync(SqliteConnection connection, string name)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name=$name";
        command.Parameters.AddWithValue("$name", name);
        return (long)(await command.ExecuteScalarAsync())! == 1;
    }
}
