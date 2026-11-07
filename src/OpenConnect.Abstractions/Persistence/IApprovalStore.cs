namespace OpenConnect.Abstractions.Persistence;

/// <summary>Provider-independent storage contract for approval state.</summary>
public interface IApprovalStore
{
    /// <summary>Gets an approval only when its tenant and principal match.</summary>
    Task<ApprovalRecord?> GetAsync(Guid id, TenantId tenantId, PrincipalId principalId, CancellationToken ct = default);

    /// <summary>Creates an approval record.</summary>
    Task<ApprovalRecord> CreateAsync(ApprovalRecord record, CancellationToken ct = default);

    /// <summary>Records a decision for an approval belonging to the specified principal and tenant.</summary>
    Task DecideAsync(Guid id, TenantId tenantId, PrincipalId principalId, string decision, CancellationToken ct = default);
}
