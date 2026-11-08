namespace OpenConnect.Persistence.EFCore.Entities;

/// <summary>
/// Durable single-use approval nonce binding (WF-04 durable).
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="Nonce"/> is an opaque caller-supplied value stored verbatim; this
/// persistence slice never derives, hashes, or transforms it.
/// </para>
/// <para>
/// Bindings are immutable and insert-only: once <c>(TenantId, Nonce)</c> is written it
/// is never updated or deleted in this slice. Tenant isolation is enforced server-side
/// on every operation; reads verify the principal and return <see langword="null"/> on
/// any mismatch (fail-closed).
/// </para>
/// </remarks>
internal sealed class ApprovalNonceEntity
{
    public string TenantId { get; set; } = string.Empty;

    public string Nonce { get; set; } = string.Empty;

    public Guid ApprovalId { get; set; }

    public string PrincipalId { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }
}
