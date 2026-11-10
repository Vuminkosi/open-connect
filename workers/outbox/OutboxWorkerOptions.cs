using Microsoft.Extensions.Options;

namespace OpenConnect.WorkerHosts.Outbox;

/// <summary>Selects whether outbox dispatch runs here or in an embedded gateway dispatcher.</summary>
public enum OutboxHostingMode
{
    /// <summary>An embedded dispatcher (for example in the gateway) owns dispatch; this host must not start dispatch.</summary>
    Embedded = 0,
    /// <summary>This host is the sole dispatcher. Requires the embedded dispatcher to be disabled.</summary>
    Standalone = 1,
}

/// <summary>Configuration for the outbox relay host. Binds <c>WorkerHosts:Outbox</c>.</summary>
public sealed class OutboxWorkerOptions
{
    /// <summary>Configuration section path.</summary>
    public const string SectionName = "WorkerHosts:Outbox";

    /// <summary>Resource key of the durable supervisor lease that fences replicas.</summary>
    public const string SupervisorResourceKey = "oc/outbox/supervisor";

    /// <summary>Gets or sets the hosting mode. Defaults to standalone dispatch.</summary>
    public OutboxHostingMode Mode { get; set; } = OutboxHostingMode.Standalone;

    /// <summary>Gets or sets whether an embedded gateway dispatcher is enabled. Must be false in standalone mode.</summary>
    public bool EmbeddedDispatcherEnabled { get; set; }

    /// <summary>Gets or sets the tenant ULID (26-character Crockford Base32) that scopes the supervisor lease.</summary>
    public string TenantUlid { get; set; } = string.Empty;

    /// <summary>Gets or sets the EF provider. One of <c>npgsql</c> (default, same as the gateway) or <c>sqlite</c>.</summary>
    public string DatabaseProvider { get; set; } = "npgsql";

    /// <summary>Gets or sets the database connection string. Never logged.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Gets or sets the supervisor lease duration.</summary>
    public TimeSpan SupervisorLeaseDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets or sets the relay polling interval.</summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Gets or sets the maximum entries per relay batch (1-1000).</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>Gets or sets the NATS server URL used for JetStream publishes.</summary>
    public string NatsUrl { get; set; } = "nats://localhost:4222";

    /// <summary>Gets or sets the JetStream subject envelopes are published to.</summary>
    public string Subject { get; set; } = "oc.v1.notifications.inapp";
}

/// <summary>Fail-fast validation for <see cref="OutboxWorkerOptions"/>.</summary>
public sealed class OutboxWorkerOptionsValidator : IValidateOptions<OutboxWorkerOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, OutboxWorkerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        if (options.Mode == OutboxHostingMode.Standalone && options.EmbeddedDispatcherEnabled)
            failures.Add("outbox_standalone_requires_embedded_disabled: Standalone mode requires EmbeddedDispatcherEnabled=false so two dispatchers never publish the same entry.");
        try { _ = Abstractions.Ulid.Parse(options.TenantUlid); }
        catch (Exception) { failures.Add("outbox_tenant_ulid_invalid: TenantUlid must be a 26-character Crockford Base32 ULID."); }
        if (!string.Equals(options.DatabaseProvider, "npgsql", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(options.DatabaseProvider, "sqlite", StringComparison.OrdinalIgnoreCase))
            failures.Add("outbox_db_provider_invalid: DatabaseProvider must be npgsql or sqlite.");
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
            failures.Add("outbox_connection_string_missing: a database connection string is required (same EF provider/schema as the gateway).");
        if (options.SupervisorLeaseDuration < TimeSpan.FromSeconds(5) || options.SupervisorLeaseDuration > TimeSpan.FromHours(1))
            failures.Add("outbox_lease_duration_invalid: SupervisorLeaseDuration must be between 5 seconds and 1 hour.");
        if (options.PollingInterval < TimeSpan.FromMilliseconds(100) || options.PollingInterval > TimeSpan.FromMinutes(5))
            failures.Add("outbox_poll_interval_invalid: PollingInterval must be between 100ms and 5 minutes.");
        if (options.BatchSize is < 1 or > 1000)
            failures.Add("outbox_batch_invalid: BatchSize must be between 1 and 1000.");
        if (string.IsNullOrWhiteSpace(options.NatsUrl) || !Uri.TryCreate(options.NatsUrl, UriKind.Absolute, out _))
            failures.Add("outbox_nats_url_invalid: NatsUrl must be an absolute NATS server URL.");
        if (string.IsNullOrWhiteSpace(options.Subject))
            failures.Add("outbox_subject_missing: a JetStream publish subject is required.");
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
