using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

[Collection("Cloud PostgreSQL")]
public sealed class CloudMigrationScriptPostgresTests
{
    [CloudPostgresFact]
    public async Task WorkerPayloadMigrationIdempotentScriptExecutesOnMigratedDatabase()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE"))
            .Options;
        await using var db = new CloudControlPlaneDbContext(options);
        await db.Database.MigrateAsync();
        var script = db.GetService<IMigrator>().GenerateScript(
            "20261120185126_AddExecutionConsumption",
            "20261120190329_BindWorkerPayload",
            MigrationsSqlGenerationOptions.Idempotent);

        await db.Database.ExecuteSqlRawAsync(script);
        await db.Database.ExecuteSqlRawAsync(script);
    }
}
