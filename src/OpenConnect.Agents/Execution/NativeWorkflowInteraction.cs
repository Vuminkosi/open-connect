using Microsoft.Agents.AI.Workflows;

namespace OpenConnect.Agents.Execution;

/// <summary>Drives native workflow external requests in-process under a host authorization decision.</summary>
/// <remarks>This adapter provides no persistence, identity, or authorization policy of its own.</remarks>
public sealed class NativeWorkflowInteraction : IAsyncDisposable
{
    private readonly StreamingRun _run;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly SemaphoreSlim _cancelGate = new(1, 1);
    private readonly HashSet<string> _consumedRequestIds = new(StringComparer.Ordinal);
    private ExternalRequest? _pendingRequest;
    private bool _started;
    private bool _completed;
    private bool _disposed;
    private bool _responseInFlight;

    private NativeWorkflowInteraction(StreamingRun run) => _run = run;

    /// <summary>Gets the actual external request at the current native workflow halt, if any.</summary>
    public ExternalRequest? PendingExternalRequest => Volatile.Read(ref _pendingRequest);

    /// <summary>Starts a host-constructed native workflow without synthesizing an agent turn.</summary>
    public static async Task<NativeWorkflowInteraction> StartAsync<TInput>(
        Workflow workflow,
        TInput trustedInput,
        CancellationToken cancellationToken = default)
        where TInput : notnull
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(trustedInput);
        cancellationToken.ThrowIfCancellationRequested();
        var run = await InProcessExecution.Default.RunStreamingAsync(
            workflow, trustedInput, cancellationToken: cancellationToken).ConfigureAwait(false);
        var interaction = new NativeWorkflowInteraction(run);
        interaction._started = true;
        try
        {
            await interaction.ObserveUntilHaltAsync(cancellationToken).ConfigureAwait(false);
            return interaction;
        }
        catch
        {
            await interaction.CancelAndDisposeRunAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Accepts one response for the exact currently pending native request.</summary>
    /// <param name="request">The request instance returned by the native workflow.</param>
    /// <param name="responseData">Host-provided response payload validated by the request's native port.</param>
    /// <param name="authorize">Required host callback; it must validate actor, scope, grants, arguments, nonce, expiry, and effect boundaries.
    /// Invoked outside the interaction lock and must not call back into this instance.
    /// A re-entrant <see cref="RespondAsync"/> fails fast with <see cref="InvalidOperationException"/>
    /// instead of deadlocking; a re-entrant <see cref="CancelAsync"/> or <see cref="DisposeAsync"/>
    /// remains preemptive and invalidates the in-flight claim, so the outer Respond then fails
    /// closed on revalidation.
    /// Only one authorization runs at a time; concurrent responses are rejected before authorize is invoked.</param>
    /// <param name="cancellationToken">Cancellation request for authorization and continuation.</param>
    public async Task RespondAsync(
        ExternalRequest request,
        object responseData,
        Func<ExternalRequest, CancellationToken, ValueTask<bool>> authorize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(responseData);
        ArgumentNullException.ThrowIfNull(authorize);

        // F2 claim-then-verify: claim the pending slot under the lock, release the
        // lock while the host authorizer runs, then re-validate under the lock.
        // State transitions (ReferenceEquals, consumed ids, completed flags) stay
        // under _operationLock; authorize, SendResponse, and the observe watch run
        // outside it so a slow or re-entrant authorizer cannot wedge the lock.
        ExternalRequest claimed;
        string claimedRequestId;
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(request, _pendingRequest))
            {
                throw new InvalidOperationException("The request is stale or is not the current pending request.");
            }

            if (_responseInFlight)
            {
                throw new InvalidOperationException("A response for the current pending request is already in progress.");
            }

            _responseInFlight = true;
            claimed = request;
            claimedRequestId = request.RequestId;
        }
        finally
        {
            _operationLock.Release();
        }

        try
        {
            // Deny-before-send: authorize runs before any response is created or sent.
            if (!await authorize(claimed, cancellationToken).ConfigureAwait(false))
            {
                throw new UnauthorizedAccessException("The host did not authorize this workflow response.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfUnavailable();
                cancellationToken.ThrowIfCancellationRequested();
                if (!ReferenceEquals(claimed, _pendingRequest))
                {
                    throw new InvalidOperationException("The request is no longer pending.");
                }

                if (_consumedRequestIds.Contains(claimedRequestId))
                {
                    throw new InvalidOperationException("The request has already been answered.");
                }
            }
            finally
            {
                _operationLock.Release();
            }

            // Create from the native request, never from caller-supplied request identity.
            var response = claimed.CreateResponse(responseData);
            await _run.SendResponseAsync(response).ConfigureAwait(false);

            // Commit the exactly-once consumption without honoring cancellation so a
            // racing cancel cannot lose replay protection for an already-sent response.
            await _operationLock.WaitAsync().ConfigureAwait(false);
            try
            {
                _consumedRequestIds.Add(claimedRequestId);
                if (ReferenceEquals(claimed, _pendingRequest))
                {
                    Volatile.Write(ref _pendingRequest, null);
                }
            }
            finally
            {
                _operationLock.Release();
            }

            try
            {
                await ObserveUntilHaltAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await CancelRunUnderGateAsync().ConfigureAwait(false);
                await _operationLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    Volatile.Write(ref _pendingRequest, null);
                    _completed = true;
                }
                finally
                {
                    _operationLock.Release();
                }

                throw;
            }
        }
        finally
        {
            await _operationLock.WaitAsync().ConfigureAwait(false);
            try
            {
                _responseInFlight = false;
            }
            finally
            {
                _operationLock.Release();
            }
        }
    }

    /// <summary>Cancels the native run explicitly; cancelling a stream alone does not stop its run.</summary>
    /// <remarks>Preemptive: only the state transition takes <c>_operationLock</c>;
    /// the run cancellation runs under a separate cancel gate so it never waits for
    /// an in-flight <see cref="RespondAsync"/> continuation.</remarks>
    public async Task CancelAsync()
    {
        await _cancelGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _operationLock.WaitAsync().ConfigureAwait(false);
            bool shouldCancel;
            try
            {
                if (_disposed || _completed) return;
                Volatile.Write(ref _pendingRequest, null);
                _completed = true;
                shouldCancel = true;
            }
            finally
            {
                _operationLock.Release();
            }

            if (shouldCancel)
            {
                await _run.CancelRunAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _cancelGate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>Preemptive: state transitions take <c>_operationLock</c> briefly;
    /// run cancellation and disposal run under a separate cancel gate so disposal
    /// never waits for an in-flight <see cref="RespondAsync"/> continuation.</remarks>
    public async ValueTask DisposeAsync()
    {
        await _cancelGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _operationLock.WaitAsync().ConfigureAwait(false);
            bool needCancel;
            try
            {
                if (_disposed) return;
                needCancel = !_completed;
                Volatile.Write(ref _pendingRequest, null);
                _completed = true;
                _disposed = true;
            }
            finally
            {
                _operationLock.Release();
            }

            if (needCancel)
            {
                await _run.CancelRunAsync().ConfigureAwait(false);
            }

            await _run.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _cancelGate.Release();
        }
    }

    private async Task ObserveUntilHaltAsync(CancellationToken cancellationToken)
    {
        // A no-pending run that yields output ends its stream exactly like a
        // pending-request halt does under blockOnPendingRequest: false, so an
        // observed output event distinguishes terminal completion from a stall.
        // Runs outside _operationLock; every shared-state transition below takes
        // the lock briefly so Cancel/Dispose stay preemptive.
        var sawOutput = false;
        await foreach (var workflowEvent in _run.WatchStreamAsync(
            blockOnPendingRequest: false, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (workflowEvent is WorkflowErrorEvent errorEvent)
            {
                throw errorEvent.Exception ?? new InvalidOperationException("Native workflow execution failed.");
            }

            if (workflowEvent is WorkflowOutputEvent)
            {
                sawOutput = true;
            }

            if (workflowEvent is RequestInfoEvent requestInfo)
            {
                // The native stream replays request events on each watch; a replay of an
                // already-answered RequestId is stale and must never re-arm pending. A
                // genuinely new halt carries a different RequestId and is still surfaced.
                // A stale-only halt on a non-completed run falls through to the
                // halted-without-pending failure below rather than a silent null.
                await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (_consumedRequestIds.Contains(requestInfo.Request.RequestId))
                    {
                        continue;
                    }

                    if (_pendingRequest is not null)
                    {
                        throw new InvalidOperationException("The native workflow reported multiple pending requests.");
                    }

                    Volatile.Write(ref _pendingRequest, requestInfo.Request);
                }
                finally
                {
                    _operationLock.Release();
                }
            }
        }

        var status = await _run.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        // Ended is the explicit terminal state; Idle is halted with no outstanding
        // requests, which this host cannot advance (it answers pending requests
        // only). Observed output additionally covers a status that has not yet
        // flipped from a transient value at stream end. A PendingRequests status
        // with no observed request stays fail-closed below, as does any other
        // non-terminal status without output (genuinely stalled runs).
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_pendingRequest is null &&
                (status == RunStatus.Ended || status == RunStatus.Idle ||
                 (sawOutput && status != RunStatus.PendingRequests)))
            {
                _completed = true;
            }

            if (_pendingRequest is null && !_completed)
            {
                throw new InvalidOperationException("Native workflow stream halted without a pending request or successful completion.");
            }
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private void ThrowIfUnavailable()
    {
        // Callers must hold _operationLock; keeps ReferenceEquals, consumed-id,
        // and completed/disposed transitions atomic and fail-closed.
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_started || _completed) throw new InvalidOperationException("The native workflow is no longer accepting responses.");
        if (_pendingRequest is null) throw new InvalidOperationException("The native workflow has no pending request.");
    }

    private async Task CancelRunUnderGateAsync()
    {
        // Serializes CancelRunAsync callers (explicit cancel, dispose, and the
        // Respond cancellation path) without holding _operationLock.
        await _cancelGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _run.CancelRunAsync().ConfigureAwait(false);
        }
        finally
        {
            _cancelGate.Release();
        }
    }

    private async ValueTask CancelAndDisposeRunAsync()
    {
        await _run.CancelRunAsync().ConfigureAwait(false);
        await _run.DisposeAsync().ConfigureAwait(false);
    }
}
