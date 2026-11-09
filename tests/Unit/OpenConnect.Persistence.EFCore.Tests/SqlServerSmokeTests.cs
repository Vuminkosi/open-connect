using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Persistence;
using OpenConnect.Persistence.EFCore.Context;
using OpenConnect.Persistence.EFCore.Entities;
using OpenConnect.Persistence.EFCore.Outbox;
using OpenConnect.Persistence.EFCore.Stores;
using Xunit;
using Xunit.Abstractions;

namespace OpenConnect.Persistence.EFCore.Tests;

public sealed class SqlServerSmokeTests(ITestOutputHelper output)
{
    private const string DefaultConnectionString = "Server=127.0.0.1,51434;Database=oc_smoke;User Id=sa;Password=Str0ng!Passw0rd!;TrustServerCertificate=True;Connect Timeout=2";
    private static readonly bool RequireProviderTests = string.Equals(Environment.GetEnvironmentVariable("OPENCONNECT_REQUIRE_PROVIDER_TESTS"), "true", StringComparison.OrdinalIgnoreCase);
    private static readonly string? ConfiguredConnectionString = Environment.GetEnvironmentVariable("OPENCONNECT_TEST_SQLSERVER_CONNECTION_STRING");
    private static readonly string ConnectionString = ConfiguredConnectionString ?? DefaultConnectionString;
    private static OpenConnectDbContext CreateContext() => new(new DbContextOptionsBuilder<OpenConnectDbContext>().UseSqlServer(ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "openconnect")).Options);

    [Fact]
    public async Task SqlServerSmoke_CreateScript_has_expected_openconnect_tables()
    {
        if (!await ServiceAvailableAsync()) return;
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
        Assert.Equal(12, db.Model.GetEntityTypes().Count(e => e.GetSchema() == "openconnect"));
        Assert.Equal(12, System.Text.RegularExpressions.Regex.Matches(script, @"CREATE TABLE").Count);
        Assert.Equal(12, db.Model.GetEntityTypes().Count());
        Assert.Contains("NativeAgentSessions", script);
        Assert.Contains("PublicSessionMappings", script);
        Assert.Contains("ApprovalNonces", script);
    }

    [Fact]
    public async Task SqlServerSmoke_RoundTrip_persists_session_task_and_outbox()
    {
        if (!await ServiceAvailableAsync()) return;
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
    private async Task<bool> ServiceAvailableAsync()
    {
        if (RequireProviderTests && string.IsNullOrWhiteSpace(ConfiguredConnectionString))
            throw new Xunit.Sdk.XunitException("SQL Server provider is required but OPENCONNECT_TEST_SQLSERVER_CONNECTION_STRING is not configured.");

        try
        {
            await using var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync();
            return true;
        }
        catch (Exception exception) when (exception is SqlException or System.Net.Sockets.SocketException or OperationCanceledException)
        {
            if (RequireProviderTests)
                throw new Xunit.Sdk.XunitException($"SQL Server provider is required but unavailable ({exception.GetType().Name}).");
            output.WriteLine($"SKIP SQL Server: unavailable ({exception.GetType().Name}).");
            return false;
        }
    }
}
