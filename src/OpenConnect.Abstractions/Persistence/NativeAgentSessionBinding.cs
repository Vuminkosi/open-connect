namespace OpenConnect.Abstractions.Persistence;

/// <summary>Identifies the exact tenant, principal, native session, agent definition, and provider compatibility scope for stored native session data.</summary>
/// <remarks>String metadata is preserved and compared ordinally and case-sensitively. A binding is authorization context, not proof of authorization.</remarks>
public sealed record NativeAgentSessionBinding
{
    /// <summary>Creates a binding for an exact identity and compatibility scope.</summary>
    /// <param name="SessionId">The non-default native session identifier.</param>
    /// <param name="TenantId">The non-default tenant identifier.</param>
    /// <param name="PrincipalId">The non-default principal identifier.</param>
    /// <param name="AgentId">The non-empty agent identifier; preserved without trimming or case folding.</param>
    /// <param name="DefinitionRevision">The non-empty agent definition revision; preserved without trimming or case folding.</param>
    /// <param name="MafVersion">The non-empty MAF version; preserved without trimming or case folding.</param>
    /// <param name="SchemaVersion">The non-empty native state schema version; preserved without trimming or case folding.</param>
    /// <param name="ProviderId">The non-empty provider identifier; preserved without trimming or case folding.</param>
    /// <exception cref="ArgumentException">An identifier is its default value or a string metadata value is null, empty, or whitespace.</exception>
    public NativeAgentSessionBinding(
        SessionId SessionId,
        TenantId TenantId,
        PrincipalId PrincipalId,
        string AgentId,
        string DefinitionRevision,
        string MafVersion,
        string SchemaVersion,
        string ProviderId)
    {
        if (SessionId.Value == default) throw new ArgumentException("A non-default session identifier is required.", nameof(SessionId));
        if (TenantId.Value == default) throw new ArgumentException("A non-default tenant identifier is required.", nameof(TenantId));
        if (PrincipalId.Value == default) throw new ArgumentException("A non-default principal identifier is required.", nameof(PrincipalId));

        ArgumentException.ThrowIfNullOrWhiteSpace(AgentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(DefinitionRevision);
        ArgumentException.ThrowIfNullOrWhiteSpace(MafVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(SchemaVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(ProviderId);

        this.SessionId = SessionId;
        this.TenantId = TenantId;
        this.PrincipalId = PrincipalId;
        this.AgentId = AgentId;
        this.DefinitionRevision = DefinitionRevision;
        this.MafVersion = MafVersion;
        this.SchemaVersion = SchemaVersion;
        this.ProviderId = ProviderId;
    }

    /// <summary>Gets the native session identifier.</summary>
    public SessionId SessionId { get; }

    /// <summary>Gets the tenant identifier.</summary>
    public TenantId TenantId { get; }

    /// <summary>Gets the principal identifier.</summary>
    public PrincipalId PrincipalId { get; }

    /// <summary>Gets the exact agent identifier.</summary>
    public string AgentId { get; }

    /// <summary>Gets the exact definition revision.</summary>
    public string DefinitionRevision { get; }

    /// <summary>Gets the exact MAF version.</summary>
    public string MafVersion { get; }

    /// <summary>Gets the exact native state schema version.</summary>
    public string SchemaVersion { get; }

    /// <summary>Gets the exact provider identifier.</summary>
    public string ProviderId { get; }
}
