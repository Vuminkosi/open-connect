namespace OpenConnect.Docs.Cloud;

public sealed class CloudCapabilityDefinition
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string Description { get; set; }
    public required string ContractVersion { get; set; }
    public required string BillingUnit { get; set; }
    public required string PlatformAvailability { get; set; }
    public required string Dependencies { get; set; }
    public required string Modalities { get; set; }
    public bool ModalitiesVerified { get; set; }
    public required string AllowedRegions { get; set; }
    public bool AllowedRegionsVerified { get; set; }
}

public sealed class CloudTenantEntitlement
{
    public Guid TenantId { get; set; }
    public required string CapabilityId { get; set; }
    public bool PlanGranted { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public int Revision { get; set; }
}

public sealed class CloudTenantCapabilitySelection
{
    public Guid TenantId { get; set; }
    public required string CapabilityId { get; set; }
    public bool Enabled { get; set; }
    public int Revision { get; set; }
}

public sealed class CloudApplicationCapabilitySelection
{
    public Guid TenantId { get; set; }
    public Guid ApplicationId { get; set; }
    public required string CapabilityId { get; set; }
    public bool Enabled { get; set; }
    public int Revision { get; set; }
}
