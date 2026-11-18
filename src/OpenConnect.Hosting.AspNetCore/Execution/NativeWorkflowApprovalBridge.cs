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
/// <para>Nonce replay protection here is process-local and per-bridge-instance by default. A durable approval store
/// is explicitly out of scope for the default path and is tracked as future work (WF-04); do not treat the default
/// bridge as a cross-process exactly-once mechanism. Pass a durable <c>IApprovalNonceStore</c>-backed consume
/// delegate to the overload below for cross-instance single-use protection with identical check ordering.</para>
/// </remarks>
public sealed class NativeWorkflowApprovalBridge(
    NativeWorkflowInteraction interaction,
    NativeWorkflowApprovalScope expectedScope,
    string expectedArgumentsHash,
    string expectedToolRevision,
    Func<CancellationToken, ValueTask<bool>>? isExecutionRevokedAsync,
    Func<CancellationToken, ValueTask<bool>>? isGrantValidAsync,
    TimeProvider? timeProvider)
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
    private Func<string, string, CancellationToken, ValueTask<bool>>? _durableTryConsumeNonceAsync;

    /// <summary>Creates a bridge with default revocation, grant, and clock behavior.</summary>
    public NativeWorkflowApprovalBridge(
        NativeWorkflowInteraction interaction,
        NativeWorkflowApprovalScope expectedScope,
        string expectedArgumentsHash,
        string expectedToolRevision)
        : this(interaction, expectedScope, expectedArgumentsHash, expectedToolRevision, null, null, null)
    {
    }

    /// <summary>Creates a bridge whose single-use nonces are consumed through a durable store.</summary>
    /// <remarks>
    /// The delegate binds <c>(nonce, requestId, cancellationToken)</c> and must return <c>true</c> only
    /// when the nonce was newly bound. Back it with <c>IApprovalNonceStore.TryConsumeAsync</c> using the
    /// expected scope tenant/principal, storing the native request id verbatim (never coerced): return
    /// <c>true</c> only on <c>Consumed</c>; <c>AlreadyConsumed</c> and <c>Conflict</c> deny the replay.
    /// Check ordering is identical to the default path, and the in-memory map is retained as a local
    /// mirror/fallback. A denied attempt consumes nothing in either map.
    /// </remarks>
    public NativeWorkflowApprovalBridge(
        NativeWorkflowInteraction interaction,
        NativeWorkflowApprovalScope expectedScope,
        string expectedArgumentsHash,
        string expectedToolRevision,
        Func<CancellationToken, ValueTask<bool>>? isExecutionRevokedAsync,
        Func<CancellationToken, ValueTask<bool>>? isGrantValidAsync,
        TimeProvider? timeProvider,
        Func<string, string, CancellationToken, ValueTask<bool>> tryConsumeNonceAsync)
        : this(interaction, expectedScope, expectedArgumentsHash, expectedToolRevision, isExecutionRevokedAsync, isGrantValidAsync, timeProvider)
    {
        _durableTryConsumeNonceAsync = tryConsumeNonceAsync ?? throw new ArgumentNullException(nameof(tryConsumeNonceAsync));
    }

    /// <summary>Gets the current pending native request, if any.</summary>
    public ExternalRequest? PendingRequest => _interaction.PendingExternalRequest;

    /// <summary>Approves one pending native request after revalidating the host decision immediately before continuation.</summary>
    /// <exception cref="UnauthorizedAccessException">The host decision denies, expired, revoked, lost its grant, or mismatched scope, arguments, or tool revision; also surfaces for a duplicate nonce whose race is lost inside AuthorizeAsync (RespondAsync converts the false authorization).</exception>
    /// <exception cref="InvalidOperationException">The request is stale, already answered, or the workflow no longer accepts responses; also thrown for a duplicate nonce caught by the fast pre-check before RespondAsync runs.</exception>
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
            // Durable delegate (when present) is consulted at this same gate so cross-instance
            // replays deny even though this process-local map cannot see them; the local map is
            // then mirrored as a fast path and retained as the fallback when no delegate exists.
            if (_durableTryConsumeNonceAsync is not null)
            {
                if (!await _durableTryConsumeNonceAsync(decision.Nonce, claimed.RequestId, authorizeCancellationToken).ConfigureAwait(false))
                    return false;
                _consumedNonces.TryAdd(decision.Nonce, 0);
            }
            else if (!_consumedNonces.TryAdd(decision.Nonce, 0))
            {
                return false;
            }

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
