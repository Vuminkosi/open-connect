using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Persistence;
using OpenConnect.Persistence.EFCore.Context;
using OpenConnect.Persistence.EFCore.Stores;
using Xunit;

namespace OpenConnect.Persistence.EFCore.Tests;

public sealed class NativeAgentSessionStoreTests
{
    [Fact]
    public async Task Native_session_store_persists_ciphertext_and_enforces_exact_version_across_contexts()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var create = Context(connection)) await create.Database.EnsureCreatedAsync();
        var binding = Binding();
        await using (var first = Context(connection))
        {
            var store = new EfNativeAgentSessionStore<OpenConnectDbContext>(first);
            Assert.Equal(NativeAgentSessionSaveOutcome.Saved, await store.SaveAsync(Record(binding, 1, [1, 2]), 0));
        }

        await using (var stale = Context(connection))
        {
            var result = await new EfNativeAgentSessionStore<OpenConnectDbContext>(stale).SaveAsync(Record(binding, 1, [3]), 0);
            Assert.Equal(NativeAgentSessionSaveOutcome.Conflict, result);
        }
        await using (var current = Context(connection))
        {
            var store = new EfNativeAgentSessionStore<OpenConnectDbContext>(current);
            var saved = await store.GetAsync(binding);
            Assert.NotNull(saved);
            Assert.Equal(1, saved.Version);
            Assert.Equal(new byte[] { 1, 2 }, saved.GetCiphertextCopy());
            Assert.Equal(NativeAgentSessionSaveOutcome.Saved, await store.SaveAsync(Record(binding, 2, [4]), 1));
        }
    }

    [Fact]
    public async Task Native_session_store_distinguishes_case_sensitive_binding_values()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = Context(connection);
        await db.Database.EnsureCreatedAsync();
        var original = Binding(agent: "agent");
        var differentCase = Binding(agent: "Agent");
        var store = new EfNativeAgentSessionStore<OpenConnectDbContext>(db);
        Assert.Equal(NativeAgentSessionSaveOutcome.Saved, await store.SaveAsync(Record(original, 1, [7]), 0));
        Assert.Null(await store.GetAsync(differentCase));
        Assert.Equal(NativeAgentSessionSaveOutcome.Conflict, await store.SaveAsync(Record(differentCase, 2, [8]), 1));
    }

    [Fact]
    public async Task Native_session_store_rejects_invalid_versions_before_write()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = Context(connection);
        await db.Database.EnsureCreatedAsync();
        var store = new EfNativeAgentSessionStore<OpenConnectDbContext>(db);
        var binding = Binding();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.SaveAsync(Record(binding, 1, [1]), -1));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(Record(binding, 2, [1]), 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.SaveAsync(Record(binding, long.MaxValue, [1]), long.MaxValue));
        Assert.Null(await store.GetAsync(binding));
    }

    [Fact]
    public async Task Native_session_store_parallel_create_has_one_winner()
    {
        var file = Path.Combine(Path.GetTempPath(), $"native-session-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<OpenConnectDbContext>().UseSqlite($"Data Source={file};Pooling=False").Options;
            await using (var setup = new OpenConnectDbContext(options)) await setup.Database.EnsureCreatedAsync();
            var binding = Binding();
            async Task<NativeAgentSessionSaveOutcome> Create(byte ciphertext)
            {
                await using var db = new OpenConnectDbContext(options);
                return await new EfNativeAgentSessionStore<OpenConnectDbContext>(db).SaveAsync(Record(binding, 1, [ciphertext]), 0);
            }
            var results = await Task.WhenAll(Create(1), Create(2));
            Assert.Single(results, result => result == NativeAgentSessionSaveOutcome.Saved);
            Assert.Single(results, result => result == NativeAgentSessionSaveOutcome.Conflict);
        }
        finally { if (File.Exists(file)) File.Delete(file); }
    }

    private static OpenConnectDbContext Context(SqliteConnection connection) => new(
        new DbContextOptionsBuilder<OpenConnectDbContext>().UseSqlite(connection).Options);

    private static NativeAgentSessionBinding Binding(string agent = "agent") => new(
        new SessionId(Ulid.NewUlid()), new TenantId(Ulid.NewUlid()), new PrincipalId(Ulid.NewUlid()),
        agent, "revision", "maf", "schema", "provider");

    private static NativeAgentSessionRecord Record(NativeAgentSessionBinding binding, long version, byte[] cipher) =>
        new(binding, version, cipher);
}
