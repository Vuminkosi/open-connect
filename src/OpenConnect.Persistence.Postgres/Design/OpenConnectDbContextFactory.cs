using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OpenConnect.Persistence.Postgres.Design;

/// <summary>Creates the PostgreSQL context for Entity Framework design-time tools.</summary>
public sealed class OpenConnectDbContextFactory : IDesignTimeDbContextFactory<OpenConnectDbContext>
{
    /// <inheritdoc />
    public OpenConnectDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__OpenConnect");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            var host = Environment.GetEnvironmentVariable("POSTGRES_HOST");
            var database = Environment.GetEnvironmentVariable("POSTGRES_DB");

            if (!string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(database))
            {
                var builder = new Npgsql.NpgsqlConnectionStringBuilder
                {
                    Host = host,
                    Database = database,
                    Port = int.TryParse(Environment.GetEnvironmentVariable("POSTGRES_PORT"), out var port)
                        ? port
                        : 5432
                };

                var username = Environment.GetEnvironmentVariable("POSTGRES_USER");
                var password = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD");
                if (!string.IsNullOrWhiteSpace(username)) builder.Username = username;
                if (!string.IsNullOrWhiteSpace(password)) builder.Password = password;
                connectionString = builder.ConnectionString;
            }
        }

        // This command only inspects the EF model and does not open a database connection.
        // Never use the placeholder for commands that may connect or mutate a database.
        if (string.IsNullOrWhiteSpace(connectionString) &&
            args.Any(arg => string.Equals(arg, "has-pending-model-changes", StringComparison.OrdinalIgnoreCase)))
        {
            connectionString = "Host=localhost;Database=oc_design_check";
        }

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Set ConnectionStrings__OpenConnect or POSTGRES_HOST and POSTGRES_DB for EF design-time operations.");
        }

        var options = new DbContextOptionsBuilder<OpenConnectDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new OpenConnectDbContext(options);
    }
}
