using System.Collections.Concurrent;
using Microsoft.Agents.AI.Workflows;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;

namespace OpenConnect.Hosting.AspNetCore.Execution;

/// <summary>Trusted host authority that one native workflow approval must match.</summary>
/// <remarks>Mirrors the scope shape revalidated by <c>MafHostTaskExecutor</c>: actor, tenant, session, and task.</remarks>
public sealed record NativeWorkflowApprovalScope(
    TenantId TenantId,
    PrincipalId PrincipalId,
    SessionId SessionId,
    TaskId TaskId);

/// <summary>Authenticated host decision presented for one pending native workflow request.</summary>
/// <remarks>In-process/host boundary only: this carries no wire format and is never serialized in this slice.</remarks>
public sealed record NativeWorkflowApprovalDecision(
    bool Approved,
    TenantId TenantId,
    PrincipalId PrincipalId,
    SessionId SessionId,
    TaskId TaskId,
    string RequestId,
    string ArgumentsHash,
    string ToolRevision,
    string Nonce,
    DateTimeOffset ExpiresAt);

/// <summary>Bridges one pending in-process native workflow request to an authenticated host approval.</summary>
/// <remarks>
/// <para>Every approval revalidates actor/tenant/session/task scope, current revocation and grants,
/// exact canonical arguments hash and tool revision, expiry, request binding, and nonce single-use
/// immediately before the native run continues. Deny, expiry, revocation, grant withdrawal, argument or
/// revision mismatch, stale binding, and duplicate or replayed nonces never continue the workflow;
/// at most one logical continuation is accepted per pending request.</para>
/// <para>Nonce replay protection here is process-local and per-bridge-instance. A durable approval store
/// is explicitly out of scope for this slice and is tracked as future work (WF-04); do not treat this
/// bridge as a cross-process exactly-once mechanism.</para>
/// </remarks>
public sealed class NativeWorkflowApprovalBridge(
    NativeWorkflowInteraction interaction,
    NativeWorkflowApprovalScope expectedScope,
    string expectedArgumentsHash,
    string expectedToolRevision,
    Func<CancellationToken, ValueTask<bool>>? isExecutionRevokedAsync = null,
    Func<CancellationToken, ValueTask<bool>>? isGrantValidAsync = null,
    TimeProvider? timeProvider = null)
{
    private readonly NativeWorkflowInteraction _interaction = interaction ?? throw new ArgumentNullException(nameof(interaction));
    private readonly NativeWorkflowApprovalScope _expectedScope = expectedScope ?? throw new ArgumentNullException(nameof(expectedScope));
    private readonly string _expectedArgumentsHash = string.IsNullOrWhiteSpace(expectedArgumentsHash)
        ? throw new ArgumentException("An expected canonical arguments hash is required.", nameof(expectedArgumentsHash))
        : expectedArgumentsHash;
    private readonly string _expectedToolRevision = string.IsNullOrWhiteSpace(expectedToolRevision)
        ? throw new ArgumentException("An expected tool revision is required.", nameof(expectedToolRevision))
        : expectedToolRevision;
    private readonly Func<CancellationToken, ValueTask<bool>>? _isExecutionRevokedAsync = isExecutionRevokedAsync;
    private readonly Func<CancellationToken, ValueTask<bool>>? _isGrantValidAsync = isGrantValidAsync;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, byte> _consumedNonces = new(StringComparer.Ordinal);

    /// <summary>Gets the current pending native request, if any.</summary>
    public ExternalRequest? PendingRequest => _interaction.PendingExternalRequest;

    /// <summary>Approves one pending native request after revalidating the host decision immediately before continuation.</summary>
    /// <exception cref="UnauthorizedAccessException">The host decision denies, expired, revoked, lost its grant, or mismatched scope, arguments, or tool revision.</exception>
    /// <exception cref="InvalidOperationException">The request is stale, already answered, duplicated, or the workflow no longer accepts responses.</exception>
    public Task ApproveAsync(
        ExternalRequest request,
        NativeWorkflowApprovalDecision decision,
        object responseData,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(responseData);
        cancellationToken.ThrowIfCancellationRequested();

        // Fail fast on identity/binding before touching the native run. The same checks run
        // again inside the authorize callback so the values actually enforced are the ones
        // current immediately before SendResponse, not the ones observed here.
        var pending = _interaction.PendingExternalRequest;
        if (!ReferenceEquals(request, pending))
            throw new InvalidOperationException("The request is stale or is not the current pending request.");
        if (!StringComparer.Ordinal.Equals(decision.RequestId, request.RequestId))
            throw new InvalidOperationException("The approval is not bound to the current pending request.");

        ThrowIfDenied(decision, _timeProvider.GetUtcNow());

        if (_consumedNonces.ContainsKey(decision.Nonce))
            throw new InvalidOperationException("The approval nonce has already been used.");

        return _interaction.RespondAsync(request, responseData, AuthorizeAsync, cancellationToken);

        async ValueTask<bool> AuthorizeAsync(ExternalRequest claimed, CancellationToken authorizeCancellationToken)
        {
            authorizeCancellationToken.ThrowIfCancellationRequested();

            // Stale identity fails closed as a programmer error, never as a silent deny.
            if (!ReferenceEquals(claimed, request))
                throw new InvalidOperationException("The request is no longer pending.");
            if (!StringComparer.Ordinal.Equals(decision.RequestId, claimed.RequestId))
                throw new InvalidOperationException("The approval is not bound to the current pending request.");

            // Revalidate everything mutable immediately before the run continues.
            ThrowIfDenied(decision, _timeProvider.GetUtcNow());

            if (_isExecutionRevokedAsync is not null &&
                await _isExecutionRevokedAsync(authorizeCancellationToken).ConfigureAwait(false))
                return false;

            if (_isGrantValidAsync is not null &&
                !await _isGrantValidAsync(authorizeCancellationToken).ConfigureAwait(false))
                return false;

            // Single-use nonce consumed only after every other check passes, immediately
            // before SendResponse. A denied attempt burns no fresh nonce; a replay loses the race here.
            if (!_consumedNonces.TryAdd(decision.Nonce, 0))
                return false;

            return decision.Approved;
        }
    }

    private void ThrowIfDenied(NativeWorkflowApprovalDecision decision, DateTimeOffset now)
    {
        if (!decision.Approved)
            throw new UnauthorizedAccessException("The host did not approve this workflow response.");
        if (decision.TenantId != _expectedScope.TenantId ||
            decision.PrincipalId != _expectedScope.PrincipalId ||
            decision.SessionId != _expectedScope.SessionId ||
            decision.TaskId != _expectedScope.TaskId)
            throw new UnauthorizedAccessException("The approval scope does not match the authorized host scope.");
        if (!StringComparer.Ordinal.Equals(decision.ArgumentsHash, _expectedArgumentsHash))
            throw new UnauthorizedAccessException("The approval arguments do not match the authorized canonical arguments.");
        if (!StringComparer.Ordinal.Equals(decision.ToolRevision, _expectedToolRevision))
            throw new UnauthorizedAccessException("The approval tool revision does not match the authorized revision.");
        if (string.IsNullOrWhiteSpace(decision.Nonce))
            throw new UnauthorizedAccessException("The approval nonce is invalid.");
        if (decision.ExpiresAt <= now)
            throw new UnauthorizedAccessException("The approval has expired.");
    }
}
