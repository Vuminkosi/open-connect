using Xunit;

namespace OpenConnect.Persistence.EFCore.Tests;

/// <summary>Requires explicit consent to start disposable PostgreSQL containers.</summary>
public sealed class PostgresDockerFactAttribute : FactAttribute
{
    public PostgresDockerFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("OPENCONNECT_REQUIRE_POSTGRES_DOCKER_TESTS"),
                "true", StringComparison.OrdinalIgnoreCase))
            Skip = "NOT VERIFIED: set OPENCONNECT_REQUIRE_POSTGRES_DOCKER_TESTS=true with Docker and preloaded postgres:16-alpine to run live PostgreSQL tests.";
    }
}
