using Microsoft.Extensions.Options;

namespace OpenConnect.Persistence.EFCore.Configuration;

/// <summary>Validates configuration that controls the EF Core persistence adapter.</summary>
public sealed class OpenConnectEfOptionsValidator : IValidateOptions<OpenConnectEfOptions>
{
    private static readonly HashSet<string> ReservedSchemas = new(StringComparer.OrdinalIgnoreCase)
    {
        "drop", "delete", "insert", "schema", "sys", "information_schema"
    };

    public ValidateOptionsResult Validate(string? name, OpenConnectEfOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.Schema) || options.Schema.Length > 128 ||
            !IsAsciiLetter(options.Schema[0]) || options.Schema.Any(c => !IsAsciiLetter(c) && !char.IsAsciiDigit(c) && c != '_') ||
            ReservedSchemas.Contains(options.Schema))
            failures.Add("ef_schema_invalid");

        if (string.IsNullOrEmpty(options.SqliteTablePrefix) || options.SqliteTablePrefix.Length > 32 ||
            options.SqliteTablePrefix[0] is < 'a' or > 'z' ||
            options.SqliteTablePrefix.Any(c => c is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '_'))
            failures.Add("ef_sqlite_prefix_invalid");

        if (options.Migrations == OpenConnectMigrationMode.ApplyOnStartup &&
            options.IntegrationMode == OpenConnectEfIntegrationMode.ExistingDbContext)
            failures.Add("ef_migration_not_allowed");

        if (!Enum.IsDefined(options.ValidateSchema)) failures.Add("ef_schema_validation_mode_invalid");
        if (options.OutboxPollingInterval < TimeSpan.FromMilliseconds(100) || options.OutboxPollingInterval > TimeSpan.FromMinutes(5)) failures.Add("ef_outbox_poll_interval_invalid");
        if (options.OutboxLeaseDuration < TimeSpan.FromSeconds(1) || options.OutboxLeaseDuration > TimeSpan.FromHours(1)) failures.Add("ef_outbox_lease_invalid");
        if (options.OutboxBatchSize is < 1 or > 1000) failures.Add("ef_outbox_batch_invalid");
        if (options.MaxOutboxDispatchers is < 0 or > 16) failures.Add("ef_outbox_dispatcher_limit_invalid");
        if (options.OutboxMaxAttempts is < 1 or > 100) failures.Add("ef_outbox_attempt_limit_invalid");
        if (options.OutboxLeaseDuration <= options.OutboxPollingInterval && options.MaxOutboxDispatchers > 1)
            failures.Add("ef_outbox_bounds_conflict");

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static bool IsAsciiLetter(char value) => value is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z');
}
