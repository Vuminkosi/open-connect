namespace OpenConnect.Persistence.EFCore.Configuration;

public enum OpenConnectEfIntegrationMode { ExistingDbContext, DedicatedDbContext }
public enum OpenConnectMigrationMode { None, Manual, ApplyOnStartup }
public enum OpenConnectSchemaValidationMode { None, ValidateModel, ValidateSchemaAndVersion }
public enum OpenConnectPersistenceConsistency { DevelopmentOnly, DurableSingleDatabase }
public enum OpenConnectOutboxClaimStrategy { Auto, OptimisticConcurrency, ProviderOptimized }

public sealed class OpenConnectEfOptions
{
    public OpenConnectEfIntegrationMode IntegrationMode { get; set; } = OpenConnectEfIntegrationMode.ExistingDbContext;
    public string Schema { get; set; } = "openconnect";
    public string SqliteTablePrefix { get; set; } = "oc_";
    public OpenConnectMigrationMode Migrations { get; set; } = OpenConnectMigrationMode.Manual;
    public OpenConnectSchemaValidationMode ValidateSchema { get; set; } = OpenConnectSchemaValidationMode.ValidateModel;
    public OpenConnectPersistenceConsistency Consistency { get; set; } = OpenConnectPersistenceConsistency.DurableSingleDatabase;
    public OpenConnectOutboxClaimStrategy OutboxClaimStrategy { get; set; } = OpenConnectOutboxClaimStrategy.Auto;
    public TimeSpan OutboxPollingInterval { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan OutboxLeaseDuration { get; set; } = TimeSpan.FromSeconds(30);
    public int OutboxBatchSize { get; set; } = 50;
    public int MaxOutboxDispatchers { get; set; } = 2;
    public int OutboxMaxAttempts { get; set; } = 12;
    public bool RequireExistingContextModelRegistration { get; set; } = true;
}
