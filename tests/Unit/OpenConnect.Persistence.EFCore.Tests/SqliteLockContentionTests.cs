using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenConnect.Abstractions;
using OpenConnect.Persistence.EFCore.Configuration;
using OpenConnect.Persistence.EFCore.Context;
using OpenConnect.Persistence.EFCore.Entities;
using OpenConnect.Persistence.EFCore.Stores;
using Xunit;

namespace OpenConnect.Persistence.EFCore.Tests;

/// <summary>
/// Proves OC2-D-DATA-023 and EF-AT-021:
/// SQLite file-based restart, lock contention, busy timeout, and bounded retry policy.
/// </summary>
public sealed class SqliteLockContentionTests
{
    private static readonly TenantId Tenant = new(Ulid.NewUlid());
    private static readonly PrincipalId Principal = new(Ulid.NewUlid());

    [Fact]
    public async Task Sqlite_concurrent_writers_with_busy_timeout_resolve_contention_without_data_loss()
    {
        var file = Path.Combine(Path.GetTempPath(), $"openconnect-sqlite-lock-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={file};Default Timeout=30;Cache=Shared";

        try
        {
            var options = new DbContextOptionsBuilder<OpenConnectDbContext>()
                .UseSqlite(connectionString)
                .ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory, OpenConnectModelCacheKeyFactory>()
                .Options;

            // Initialize DB and WAL mode for concurrent reader/writer support
            await using (var db = new OpenConnectDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
                await db.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout=5000;");
            }

            const int workerCount = 15;
            var startBarrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var tasks = Enumerable.Range(0, workerCount).Select(async workerId =>
            {
                await startBarrier.Task;

                // Each worker creates an independent DbContext and executes an isolated write transaction with retry policy
                for (var attempt = 0; attempt < 10; attempt++)
                {
                    try
                    {
                        await using var db = new OpenConnectDbContext(options);
                        var taskId = new TaskId(Ulid.NewUlid());
                        var store = new EfTaskStore<OpenConnectDbContext>(db);

                        var accepted = await store.AcceptAsync(new TaskAcceptance(
                            taskId,
                            Tenant,
                            Principal,
                            IdempotencyKey: $"worker-{workerId}-task-{attempt}"));

                        Assert.Equal(taskId, accepted.Id);
                        break;
                    }
                    catch (SqliteException ex) when (ex.SqliteErrorCode is 5 /* SQLITE_BUSY */ or 6 /* SQLITE_LOCKED */)
                    {
                        await Task.Delay(Random.Shared.Next(10, 50));
                    }
                }
            }).ToArray();

            startBarrier.SetResult();
            await Task.WhenAll(tasks);

            // Verify all worker writes were committed safely with zero loss
            await using (var verifyDb = new OpenConnectDbContext(options))
            {
                var count = await verifyDb.Set<TaskEntity>().CountAsync();
                Assert.Equal(workerCount, count);
            }
        }
        finally
        {
            // Clear pool so SQLite releases file lock before deletion
            SqliteConnection.ClearAllPools();
            if (File.Exists(file))
            {
                try { File.Delete(file); } catch { /* best effort cleanup */ }
            }
        }
    }

    [Fact]
    public async Task Sqlite_file_database_reopens_and_replays_events_consistently()
    {
        var file = Path.Combine(Path.GetTempPath(), $"openconnect-sqlite-restart-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={file};Default Timeout=30";

        try
        {
            var options = new DbContextOptionsBuilder<OpenConnectDbContext>()
                .UseSqlite(connectionString)
                .ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory, OpenConnectModelCacheKeyFactory>()
                .Options;

            var sessionId = new SessionId(Ulid.NewUlid());

            // Phase 1: create session and write events
            await using (var db1 = new OpenConnectDbContext(options))
            {
                await db1.Database.EnsureCreatedAsync();
                var sessions = new EfSessionStore<OpenConnectDbContext>(db1);
                await sessions.UpsertAsync(new(sessionId, Tenant, Principal, 1));

                var journal = new EfEventJournal<OpenConnectDbContext>(db1);
                await journal.AppendAsync(new(sessionId, 0, "session.created", "payload-1"));
                await journal.AppendAsync(new(sessionId, 0, "turn.started", "payload-2"));
                await journal.AppendAsync(new(sessionId, 0, "turn.completed", "payload-3"));
            }

            // Phase 2: complete shutdown and reopen fresh connection
            SqliteConnection.ClearAllPools();

            await using (var db2 = new OpenConnectDbContext(options))
            {
                var sessions = new EfSessionStore<OpenConnectDbContext>(db2);
                var session = await sessions.GetAsync(sessionId, Tenant, Principal);
                Assert.NotNull(session);
                Assert.Equal(sessionId, session.Id);

                var journal = new EfEventJournal<OpenConnectDbContext>(db2);
                var replayed = new List<EventRecord>();
                await foreach (var ev in journal.ReplayAsync(sessionId))
                {
                    replayed.Add(ev);
                }

                Assert.Equal(3, replayed.Count);
                Assert.Equal(new long[] { 1, 2, 3 }, replayed.Select(e => e.Sequence).ToArray());
                Assert.Equal(new[] { "session.created", "turn.started", "turn.completed" }, replayed.Select(e => e.Type).ToArray());
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(file))
            {
                try { File.Delete(file); } catch { }
            }
        }
    }
}
