namespace OpenConnect.Abstractions.Persistence;

/// <summary>Outcome of a durable approval nonce consume attempt.</summary>
public enum ApprovalNonceConsumeOutcome
{
    /// <summary>The nonce was newly bound to the request id.</summary>
    Consumed,

    /// <summary>The identical tenant/nonce/request-id/principal binding already exists.</summary>
    AlreadyConsumed,

    /// <summary>
    /// The tenant/nonce key is already bound to a different request id or principal.
    /// Fail closed: the caller must reject the replay.
    /// </summary>
    Conflict,
}

/// <summary>Audit view of a stored approval nonce binding.</summary>
/// <param name="RequestId">The native external request id the nonce is bound to, stored verbatim.</param>
/// <param name="CreatedAtUtc">When the binding was first persisted.</param>
public sealed record ApprovalNonceBinding(string RequestId, DateTime CreatedAtUtc);

/// <summary>
/// Durable single-use approval nonce store: binds <c>(TenantId, Nonce)</c> to a native request id.
/// </summary>
/// <remarks>
/// <para>
/// Binding-type decision (a): the bound value is the native <c>ExternalRequest.RequestId</c> string,
/// stored verbatim. Implementations must not coerce it to a <see cref="Guid"/>, derive it, hash it,
/// or transform it in any way. The nonce is likewise an opaque caller-supplied value stored verbatim
/// with a maximum length of 64 characters.
/// </para>
/// <para>
/// Bindings are immutable and insert-only: only an absent <c>(TenantId, Nonce)</c> key may be
/// created, never updated or deleted. Tenant isolation is enforced server-side on every operation.
/// Reads additionally verify the principal and return <see langword="null"/> on any mismatch or
/// corrupt row so foreign or malformed bindings are indistinguishable from absent (fail-closed).
/// Callers keep their ownership checks.
/// </para>
/// </remarks>
public interface IApprovalNonceStore
{
    /// <summary>Gets the request-id binding for audit only when tenant and principal match.</summary>
    Task<ApprovalNonceBinding?> GetAsync(TenantId tenantId, PrincipalId principalId, string nonce, CancellationToken ct = default);

    /// <summary>Atomically binds a nonce to a native request id for single-use replay protection.</summary>
    /// <param name="tenantId">Owning tenant scope for the nonce binding.</param>
    /// <param name="principalId">Owning principal scope for the nonce binding.</param>
    /// <param name="nonce">Opaque caller-supplied nonce, stored verbatim; at most 64 characters.</param>
    /// <param name="requestId">Native external request id, stored verbatim; never coerced to a guid.</param>
    /// <param name="ct">Token to cancel the operation.</param>
    /// <returns><see cref="ApprovalNonceConsumeOutcome.Consumed"/> when newly bound, otherwise the exact non-write outcome.</returns>
    Task<ApprovalNonceConsumeOutcome> TryConsumeAsync(TenantId tenantId, PrincipalId principalId, string nonce, string requestId, CancellationToken ct = default);
}
