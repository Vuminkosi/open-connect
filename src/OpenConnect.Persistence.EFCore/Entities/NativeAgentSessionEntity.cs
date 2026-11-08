namespace OpenConnect.Persistence.EFCore.Entities;

/// <summary>Opaque encrypted native MAF session state scoped to one exact binding.</summary>
internal sealed class NativeAgentSessionEntity
{
    public string BindingKey { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string PrincipalId { get; set; } = string.Empty;
    public string AgentId { get; set; } = string.Empty;
    public string DefinitionRevision { get; set; } = string.Empty;
    public string MafVersion { get; set; } = string.Empty;
    public string SchemaVersion { get; set; } = string.Empty;
    public string ProviderId { get; set; } = string.Empty;
    public long Version { get; set; }
    public byte[] Ciphertext { get; set; } = [];
}
