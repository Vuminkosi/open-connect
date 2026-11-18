using Microsoft.Extensions.Options;

namespace OpenConnect.WorkerHosts.Operational;

/// <summary>Selects whether this host supervises operational runs on its own or alongside an embedded gateway supervisor.</summary>
public enum OperationalAgentHostingMode
{
    /// <summary>An embedded supervisor (for example in the gateway) owns execution leases; this host must not start supervision.</summary>
    Embedded = 0,
    /// <summary>This host is the sole supervisor. Requires the embedded supervisor to be disabled.</summary>
    Standalone = 1,
}

/// <summary>Configuration for the operational worker host. Binds <c>WorkerHosts:Operational</c>.</summary>
public sealed class OperationalWorkerOptions
{
    /// <summary>Configuration section path.</summary>
    public const string SectionName = "WorkerHosts:Operational";

    /// <summary>Resource key of the durable supervisor lease.</summary>
    public const string SupervisorResourceKey = "oc/operational/supervisor";

    /// <summary>Gets or sets the hosting mode. Defaults to standalone supervision.</summary>
    public OperationalAgentHostingMode Mode { get; set; } = OperationalAgentHostingMode.Standalone;

    /// <summary>Gets or sets whether an embedded gateway supervisor is enabled. Must be false in standalone mode.</summary>
    public bool EmbeddedEnabled { get; set; }

    /// <summary>Gets or sets the tenant ULID (26-character Crockford Base32) that scopes the supervisor lease.</summary>
    public string TenantUlid { get; set; } = string.Empty;

    /// <summary>Gets or sets the EF provider. One of <c>npgsql</c> / <c>postgres</c> (default, same as the gateway), <c>sqlserver</c>, or <c>sqlite</c>.</summary>
    public string DatabaseProvider { get; set; } = "npgsql";

    /// <summary>Gets or sets the database connection string. Never logged.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Gets or sets the supervisor lease duration.</summary>
    public TimeSpan SupervisorLeaseDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets or sets the supervisor heartbeat interval. Must be shorter than the lease duration.</summary>
    public TimeSpan RenewInterval { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>Fail-fast validation for <see cref="OperationalWorkerOptions"/>.</summary>
public sealed class OperationalWorkerOptionsValidator : IValidateOptions<OperationalWorkerOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, OperationalWorkerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        if (options.Mode == OperationalAgentHostingMode.Standalone && options.EmbeddedEnabled)
            failures.Add("operational_standalone_requires_embedded_disabled: Standalone mode requires EmbeddedEnabled=false so two supervisors never fence the same executions.");
        try { _ = Abstractions.Ulid.Parse(options.TenantUlid); }
        catch (Exception) { failures.Add("operational_tenant_ulid_invalid: TenantUlid must be a 26-character Crockford Base32 ULID."); }
        if (!string.Equals(options.DatabaseProvider, "npgsql", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(options.DatabaseProvider, "postgres", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(options.DatabaseProvider, "sqlserver", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(options.DatabaseProvider, "sqlite", StringComparison.OrdinalIgnoreCase))
            failures.Add("operational_db_provider_invalid: DatabaseProvider must be npgsql, postgres, sqlserver, or sqlite.");
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
            failures.Add("operational_connection_string_missing: a database connection string is required (same EF provider/schema as the gateway).");
        if (options.SupervisorLeaseDuration < TimeSpan.FromSeconds(5) || options.SupervisorLeaseDuration > TimeSpan.FromHours(1))
            failures.Add("operational_lease_duration_invalid: SupervisorLeaseDuration must be between 5 seconds and 1 hour.");
        if (options.RenewInterval <= TimeSpan.Zero || options.RenewInterval >= options.SupervisorLeaseDuration)
            failures.Add("operational_renew_interval_invalid: RenewInterval must be positive and shorter than SupervisorLeaseDuration.");
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
