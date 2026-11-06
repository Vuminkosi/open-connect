using Microsoft.EntityFrameworkCore;
using OpenConnect.Persistence.Postgres;
using OpenConnect.Persistence.Postgres.Entities;
using Xunit;

namespace OpenConnect.Persistence.Tests;

public sealed class PersistenceModelTests
{
    [Fact]
    public void Model_contains_expected_tables_and_optimistic_version_tokens()
    {
        var options = new DbContextOptionsBuilder<OpenConnectDbContext>()
            .UseNpgsql("Host=localhost;Database=model_only;Username=test;Password=test")
            .Options;
        using var context = new OpenConnectDbContext(options);
        var model = context.Model;

        Assert.Equal("oc_sessions", model.FindEntityType(typeof(SessionEntity))!.GetTableName());
        Assert.Equal("oc_tasks", model.FindEntityType(typeof(TaskEntity))!.GetTableName());
        Assert.True(model.FindEntityType(typeof(SessionEntity))!.FindProperty(nameof(SessionEntity.Version))!.IsConcurrencyToken);
        Assert.True(model.FindEntityType(typeof(TaskEntity))!.FindProperty(nameof(TaskEntity.Version))!.IsConcurrencyToken);
        Assert.Contains(model.GetEntityTypes(), entity => entity.GetTableName() == "oc_privacy_tokens");
    }

    [Fact(Skip = "Placeholder: exercise competing AcceptAsync requests against PostgreSQL after transactional store implementation.")]
    public Task Concurrent_accept_creates_exactly_one_task() => Task.CompletedTask;
}
