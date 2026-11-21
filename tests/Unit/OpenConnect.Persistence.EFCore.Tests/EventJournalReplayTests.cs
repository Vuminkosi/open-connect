using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Persistence;
using OpenConnect.Persistence.EFCore.Entities;
using OpenConnect.Persistence.EFCore.Stores;
using Xunit;
using ApplicationDbContext = OpenConnect.Persistence.EFCore.Tests.RelationalPersistenceTests.ApplicationDbContext;

namespace OpenConnect.Persistence.EFCore.Tests;

public sealed class EventJournalReplayTests
{
    private static readonly TenantId Tenant = new(Ulid.NewUlid());
    private static readonly PrincipalId Principal = new(Ulid.NewUlid());

    [Fact]
    public async Task Replay_reads_all_three_pages_in_order_after_a_nonzero_cursor()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var writer = Context(connection);
        await writer.Database.EnsureCreatedAsync();
        var session = await SeedAsync(writer, 457);
        _ = await SeedAsync(writer, 510);
        var probe = new EventPageProbe();
        await using var reader = Context(connection, probe);
        var journal = new EfEventJournal<ApplicationDbContext>(reader);

        var results = await CollectAsync(journal.ReplayAsync(session, 19));

        Assert.Equal(Enumerable.Range(20, 438).Select(number => Expected(session, number)), results);
        Assert.Equal(3, probe.Pages);
        Assert.All(probe.Limits, limit => Assert.Equal(200L, limit));
    }

    [Fact]
    public async Task Replay_checks_cancellation_before_fetching_a_second_page()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var writer = Context(connection);
        await writer.Database.EnsureCreatedAsync();
        var session = await SeedAsync(writer, 457);
        var probe = new EventPageProbe();
        await using var reader = Context(connection, probe);
        var journal = new EfEventJournal<ApplicationDbContext>(reader);
        using var cancellation = new CancellationTokenSource();
        await using var replay = journal.ReplayAsync(session, 19).GetAsyncEnumerator(cancellation.Token);
        for (var number = 20; number < 220; ++number)
        {
            Assert.True(await replay.MoveNextAsync());
            Assert.Equal(Expected(session, number), replay.Current);
        }

        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => replay.MoveNextAsync().AsTask());

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(1, probe.Pages);
    }

    [Fact]
    public async Task Replay_captures_its_upper_bound_on_first_move_and_defers_later_appends()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var writer = Context(connection);
        await writer.Database.EnsureCreatedAsync();
        var session = await SeedAsync(writer, 450);
        var append = new EfEventJournal<ApplicationDbContext>(writer);
        await using var reader = Context(connection);
        var replay = new EfEventJournal<ApplicationDbContext>(reader);
        await using var stream = replay.ReplayAsync(session, 19).GetAsyncEnumerator();

        Assert.Equal(451, await append.AppendAsync(Expected(session, 451)));
        Assert.True(await stream.MoveNextAsync());
        var results = new List<EventRecord> { stream.Current };
        Assert.Equal(452, await append.AppendAsync(Expected(session, 452)));
        while (await stream.MoveNextAsync())
            results.Add(stream.Current);

        Assert.Equal(Enumerable.Range(20, 432).Select(number => Expected(session, number)), results);
        Assert.Equal([Expected(session, 452)], await CollectAsync(replay.ReplayAsync(session, 451)));
    }

    [Fact]
    public async Task Replay_preserves_missing_session_and_cursor_behavior()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = Context(connection);
        await db.Database.EnsureCreatedAsync();
        var session = await SeedAsync(db, 2);
        var replay = new EfEventJournal<ApplicationDbContext>(db);

        Assert.Empty(await CollectAsync(replay.ReplayAsync(new SessionId(Ulid.NewUlid()), -1)));
        Assert.Equal([Expected(session, 1), Expected(session, 2)], await CollectAsync(replay.ReplayAsync(session, long.MinValue)));
        Assert.Empty(await CollectAsync(replay.ReplayAsync(session, 2)));
        Assert.Empty(await CollectAsync(replay.ReplayAsync(session, long.MaxValue)));
    }

    private static async Task<SessionId> SeedAsync(ApplicationDbContext db, int count)
    {
        var session = new SessionId(Ulid.NewUlid());
        await new EfSessionStore<ApplicationDbContext>(db).UpsertAsync(new SessionRecord(session, Tenant, Principal, 1));
        db.AddRange(Enumerable.Range(1, count).Select(number =>
        {
            var record = Expected(session, number);
            return new EventEntity
            {
                EventId = Guid.NewGuid(),
                TenantScope = UlidGuidMapper.Scope(Tenant.Value),
                SessionId = UlidGuidMapper.ToGuid(session.Value),
                Sequence = number,
                EventType = record.Type,
                SanitizedPayload = record.Data,
                OccurredUtc = DateTime.UnixEpoch,
            };
        }));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return session;
    }

    private static EventRecord Expected(SessionId session, int number) =>
        new(session, number, "synthetic-event", $"synthetic-payload-{number}");

    private static ApplicationDbContext Context(SqliteConnection connection, DbCommandInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection);
        if (interceptor is not null)
            options.AddInterceptors(interceptor);
        return new ApplicationDbContext(options.Options);
    }

    private static async Task<List<EventRecord>> CollectAsync(IAsyncEnumerable<EventRecord> stream)
    {
        var results = new List<EventRecord>();
        await foreach (var item in stream)
            results.Add(item);
        return results;
    }

    private sealed class EventPageProbe : DbCommandInterceptor
    {
        public List<long> Limits { get; } = [];
        public int Pages => Limits.Count;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            const string limitClause = "LIMIT ";
            var position = command.CommandText.LastIndexOf(limitClause, StringComparison.Ordinal);
            if (position >= 0 && command.CommandText.Contains("FROM \"oc_events\"", StringComparison.Ordinal))
            {
                var limit = command.CommandText[(position + limitClause.Length)..].Trim();
                var value = limit.StartsWith('@') ? command.Parameters[limit].Value : limit;
                Limits.Add(Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture));
            }
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
