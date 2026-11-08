using Microsoft.Agents.AI.Workflows;

namespace OpenConnect.Agents.Execution;

/// <summary>Drives native workflow external requests in-process under a host authorization decision.</summary>
/// <remarks>This adapter provides no persistence, identity, or authorization policy of its own.</remarks>
public sealed class NativeWorkflowInteraction : IAsyncDisposable
{
    private readonly StreamingRun _run;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly HashSet<string> _consumedRequestIds = new(StringComparer.Ordinal);
    private ExternalRequest? _pendingRequest;
    private bool _started;
    private bool _completed;
    private bool _disposed;

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
    /// <param name="authorize">Required host callback; it must validate actor, scope, grants, arguments, nonce, expiry, and effect boundaries.</param>
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
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(request, _pendingRequest))
            {
                throw new InvalidOperationException("The request is stale or is not the current pending request.");
            }

            if (!await authorize(request, cancellationToken).ConfigureAwait(false))
            {
                throw new UnauthorizedAccessException("The host did not authorize this workflow response.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(request, _pendingRequest))
            {
                throw new InvalidOperationException("The request is no longer pending.");
            }

            // Create from the native request, never from caller-supplied request identity.
            var response = request.CreateResponse(responseData);
            var consumedRequestId = request.RequestId;
            await _run.SendResponseAsync(response).ConfigureAwait(false);
            _consumedRequestIds.Add(consumedRequestId);
            Volatile.Write(ref _pendingRequest, null);
            try
            {
                await ObserveUntilHaltAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await _run.CancelRunAsync().ConfigureAwait(false);
                Volatile.Write(ref _pendingRequest, null);
                _completed = true;
                throw;
            }
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <summary>Cancels the native run explicitly; cancelling a stream alone does not stop its run.</summary>
    public async Task CancelAsync()
    {
        await _operationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || _completed) return;
            await _run.CancelRunAsync().ConfigureAwait(false);
            Volatile.Write(ref _pendingRequest, null);
            _completed = true;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _operationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            if (!_completed)
            {
                await _run.CancelRunAsync().ConfigureAwait(false);
            }

            Volatile.Write(ref _pendingRequest, null);
            _completed = true;
            _disposed = true;
            await _run.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task ObserveUntilHaltAsync(CancellationToken cancellationToken)
    {
        // A no-pending run that yields output ends its stream exactly like a
        // pending-request halt does under blockOnPendingRequest: false, so an
        // observed output event distinguishes terminal completion from a stall.
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
        }

        var status = await _run.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        // Ended is the explicit terminal state; Idle is halted with no outstanding
        // requests, which this host cannot advance (it answers pending requests
        // only). Observed output additionally covers a status that has not yet
        // flipped from a transient value at stream end. A PendingRequests status
        // with no observed request stays fail-closed below, as does any other
        // non-terminal status without output (genuinely stalled runs).
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

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_started || _completed) throw new InvalidOperationException("The native workflow is no longer accepting responses.");
        if (_pendingRequest is null) throw new InvalidOperationException("The native workflow has no pending request.");
    }

    private async ValueTask CancelAndDisposeRunAsync()
    {
        await _run.CancelRunAsync().ConfigureAwait(false);
        await _run.DisposeAsync().ConfigureAwait(false);
    }
}
