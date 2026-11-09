namespace OpenConnect.Persistence.EFCore.Entities;

/// <summary>
/// Durable public-UUID to runtime-ULID session mapping (contract SESSION-UUID-MAPPING, durable).
/// </summary>
/// <remarks>
/// <para>
/// The public <see cref="PublicId"/> is a genuine UUID supplied at session creation time.
/// It is never derived from the runtime ULID: no ULID-to-UUID conversion may be used here.
/// </para>
/// <para>
/// Mappings are immutable once written and tenant-partitioned: the primary key is
/// <c>(TenantId, PublicId)</c> and <see cref="InternalSessionId"/> is globally unique.
/// Reads must always filter by tenant and verify the principal; a mismatch is
/// indistinguishable from an absent mapping (fail-closed, resolve as session-not-found).
/// Post-resolve dual ownership checks remain the caller's responsibility.
/// </para>
/// </remarks>
internal sealed class PublicSessionMappingEntity
{
    public Guid PublicId { get; set; }

    public string InternalSessionId { get; set; } = string.Empty;

    public string TenantId { get; set; } = string.Empty;

    public string PrincipalId { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    public long Version { get; set; }
}
