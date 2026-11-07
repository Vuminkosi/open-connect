using Microsoft.EntityFrameworkCore;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Persistence;
using OpenConnect.Persistence.EFCore.Context;
using OpenConnect.Persistence.EFCore.Entities;
using OpenConnect.Persistence.EFCore.Outbox;
using OpenConnect.Persistence.EFCore.Stores;
using Xunit;

namespace OpenConnect.Persistence.EFCore.Tests;

public sealed class SqlServerSmokeTests
{
    private const string ConnectionString = "Server=127.0.0.1,51434;Database=oc_smoke;User Id=sa;Password=Str0ng!Passw0rd!;TrustServerCertificate=True";
    private static OpenConnectDbContext CreateContext() => new(new DbContextOptionsBuilder<OpenConnectDbContext>().UseSqlServer(ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "openconnect")).Options);

    [Fact]
    public void SqlServerSmoke_CreateScript_has_expected_openconnect_tables()
    {
        using var db = CreateContext();
        var script = db.Database.GenerateCreateScript();
        var lines = script.Split('\n');
        var scriptPath = Path.Combine(Path.GetTempPath(), $"efsmoke-{Guid.NewGuid():N}.sql");
        try
        {
            File.WriteAllLines(scriptPath, lines.Take(60));
        }
        finally
        {
            if (File.Exists(scriptPath)) File.Delete(scriptPath);
        }
        Assert.All(db.Model.GetEntityTypes(), e => Assert.Equal("openconnect", e.GetSchema()));
        Assert.Equal(9, db.Model.GetEntityTypes().Count(e => e.GetSchema() == "openconnect"));
        Assert.Equal(9, System.Text.RegularExpressions.Regex.Matches(script, @"CREATE TABLE").Count);
        Assert.Equal(9, db.Model.GetEntityTypes().Count());
    }

    [Fact]
    public async Task SqlServerSmoke_RoundTrip_persists_session_task_and_outbox()
    {
        await using var db = CreateContext();
        await db.Database.EnsureCreatedAsync();
        var scope = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        var sessionId = new SessionId(Ulid.NewUlid());
        await new EfSessionStore<OpenConnectDbContext>(db).UpsertAsync(new(sessionId, scope, principal, 1));
        var taskId = new TaskId(Ulid.NewUlid());
        Assert.Equal(taskId, (await new EfTaskStore<OpenConnectDbContext>(db).AcceptAsync(new(taskId, scope, principal, "mssql-smoke-key"))).Id);
        var entry = new OutboxEntry(Guid.NewGuid(), "mssql-smoke", "payload");
        await new EfOutboxStore<OpenConnectDbContext>(db).EnqueueAsync(entry);
        var pending = await new EfOutboxStore<OpenConnectDbContext>(db).GetPendingAsync(10);
        Assert.Contains(pending, item => item.Id == entry.Id && item.Payload == "payload");
    }
}
