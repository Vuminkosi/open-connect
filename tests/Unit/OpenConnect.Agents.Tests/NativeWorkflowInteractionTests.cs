using Microsoft.Agents.AI.Workflows;
using OpenConnect.Agents.Execution;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class NativeWorkflowInteractionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Native_external_request_halts_before_response_and_authorized_response_continues()
    {
        var workflow = CreateWorkflow();
        await using var interaction = await NativeWorkflowInteraction.StartAsync(workflow, "start")
            .WaitAsync(Timeout);

        var first = Assert.IsType<ExternalRequest>(interaction.PendingExternalRequest);
        Assert.True(first.TryGetDataAs<ApprovalRequest>(out var firstPayload));
        Assert.Equal("start", firstPayload!.Text);

        await interaction.RespondAsync(first, "approved", (_, _) => ValueTask.FromResult(true))
            .WaitAsync(Timeout);

        var second = Assert.IsType<ExternalRequest>(interaction.PendingExternalRequest);
        Assert.NotSame(first, second);
        Assert.Equal("approval", second.PortInfo.PortId);
        Assert.NotEqual(first.RequestId, second.RequestId);
        Assert.True(second.TryGetDataAs<ApprovalRequest>(out var secondPayload));
        Assert.Equal("approved", secondPayload!.Text);

        await interaction.CancelAsync().WaitAsync(Timeout);
        await interaction.DisposeAsync().AsTask().WaitAsync(Timeout);
    }

    [Fact]
    public async Task Denied_response_does_not_continue_native_workflow()
    {
        await using var interaction = await NativeWorkflowInteraction.StartAsync(CreateWorkflow(), "start")
            .WaitAsync(Timeout);
        var request = Assert.IsType<ExternalRequest>(interaction.PendingExternalRequest);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => interaction.RespondAsync(
            request, "approved", (_, _) => ValueTask.FromResult(false)));

        Assert.Same(request, interaction.PendingExternalRequest);
    }

    [Fact]
    public async Task Duplicate_and_stale_responses_are_rejected()
    {
        await using var interaction = await NativeWorkflowInteraction.StartAsync(CreateWorkflow(), "start")
            .WaitAsync(Timeout);
        var request = Assert.IsType<ExternalRequest>(interaction.PendingExternalRequest);
        await interaction.RespondAsync(request, "approved", (_, _) => ValueTask.FromResult(true))
            .WaitAsync(Timeout);

        await Assert.ThrowsAsync<InvalidOperationException>(() => interaction.RespondAsync(
            request, "duplicate", (_, _) => ValueTask.FromResult(true)));
    }

    [Fact]
    public async Task Concurrent_responses_accept_at_most_one_continuation()
    {
        await using var interaction = await NativeWorkflowInteraction.StartAsync(CreateWorkflow(), "start")
            .WaitAsync(Timeout);
        var request = Assert.IsType<ExternalRequest>(interaction.PendingExternalRequest);
        var authorizationCalls = 0;
        async ValueTask<bool> Authorize(ExternalRequest _, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref authorizationCalls);
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            return true;
        }

        var replies = await Task.WhenAll(
            CaptureAsync(interaction.RespondAsync(request, "first", Authorize)),
            CaptureAsync(interaction.RespondAsync(request, "second", Authorize))).WaitAsync(Timeout);

        Assert.Single(replies, succeeded => succeeded);
        Assert.Equal(1, authorizationCalls);
    }

    [Fact]
    public async Task Cancel_and_dispose_stop_a_run_waiting_for_native_external_request()
    {
        var interaction = await NativeWorkflowInteraction.StartAsync(CreateWorkflow(), "start").WaitAsync(Timeout);
        Assert.NotNull(interaction.PendingExternalRequest);

        await interaction.CancelAsync().WaitAsync(Timeout);
        Assert.Null(interaction.PendingExternalRequest);
        await interaction.DisposeAsync().AsTask().WaitAsync(Timeout);
    }

    [Fact]
    public async Task Dispose_stops_a_run_waiting_for_native_external_request()
    {
        var interaction = await NativeWorkflowInteraction.StartAsync(CreateWorkflow(), "start").WaitAsync(Timeout);
        Assert.NotNull(interaction.PendingExternalRequest);

        await interaction.DisposeAsync().AsTask().WaitAsync(Timeout);
        Assert.Null(interaction.PendingExternalRequest);
    }

    [Fact]
    public async Task Ended_workflow_has_no_pending_request_and_rejects_responses()
    {
        await using var completed = await NativeWorkflowInteraction.StartAsync(CreatePassthroughWorkflow(), "start")
            .WaitAsync(Timeout);
        Assert.Null(completed.PendingExternalRequest);

        await using var other = await NativeWorkflowInteraction.StartAsync(CreateWorkflow(), "start")
            .WaitAsync(Timeout);
        var foreign = Assert.IsType<ExternalRequest>(other.PendingExternalRequest);

        await Assert.ThrowsAsync<InvalidOperationException>(() => completed.RespondAsync(
            foreign, "approved", (_, _) => ValueTask.FromResult(true)));
        Assert.Null(completed.PendingExternalRequest);
    }

    [Fact]
    public async Task Cancel_during_slow_authorize_is_preemptive()
    {
        var interaction = await NativeWorkflowInteraction.StartAsync(CreateWorkflow(), "start")
            .WaitAsync(Timeout);
        var request = Assert.IsType<ExternalRequest>(interaction.PendingExternalRequest);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        async ValueTask<bool> SlowAuthorize(ExternalRequest _, CancellationToken cancellationToken)
        {
            entered.TrySetResult(true);
            await release.Task.WaitAsync(Timeout);
            return true;
        }

        var respondTask = interaction.RespondAsync(request, "approved", SlowAuthorize);
        await entered.Task.WaitAsync(Timeout);

        // Must complete while authorize is still blocked; the old design held the
        // operation lock across authorize and wedged here until the timeout.
        await interaction.CancelAsync().WaitAsync(Timeout);
        Assert.Null(interaction.PendingExternalRequest);

        release.TrySetResult(true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => respondTask.WaitAsync(Timeout));
        await interaction.DisposeAsync().AsTask().WaitAsync(Timeout);
    }

    [Fact]
    public async Task Reentrant_authorize_attempt_fails_fast_without_deadlock()
    {
        await using var interaction = await NativeWorkflowInteraction.StartAsync(CreateWorkflow(), "start")
            .WaitAsync(Timeout);
        var request = Assert.IsType<ExternalRequest>(interaction.PendingExternalRequest);
        async ValueTask<bool> Authorize(ExternalRequest current, CancellationToken cancellationToken)
        {
            // A re-entrant response for the claimed request must fail fast with
            // InvalidOperationException; holding the lock across authorize would
            // deadlock here and surface as a TimeoutException instead.
            await Assert.ThrowsAsync<InvalidOperationException>(() => interaction.RespondAsync(
                current, "reentrant", (_, _) => ValueTask.FromResult(true), cancellationToken).WaitAsync(Timeout));
            return true;
        }

        await interaction.RespondAsync(request, "approved", Authorize).WaitAsync(Timeout);
        var next = Assert.IsType<ExternalRequest>(interaction.PendingExternalRequest);
        Assert.NotSame(request, next);
    }

    [Fact]
    public async Task OutputPlusPending_StaysPending()
    {
        await using var interaction = await NativeWorkflowInteraction.StartAsync(CreateOutputThenHaltWorkflow(), "start")
            .WaitAsync(Timeout);

        // The intermediate output from "output-source" must not be mistaken
        // for terminal completion: the run halts on the native external request.
        var pending = Assert.IsType<ExternalRequest>(interaction.PendingExternalRequest);
        Assert.True(pending.TryGetDataAs<ApprovalRequest>(out var payload));
        Assert.Equal("start", payload!.Text);

        // The interaction is still live: an authorized response advances the
        // run to terminal completion instead of being rejected as stale/completed.
        await interaction.RespondAsync(pending, "approved", (_, _) => ValueTask.FromResult(true))
            .WaitAsync(Timeout);
        Assert.Null(interaction.PendingExternalRequest);
    }

    [Fact(Skip = "NOT VERIFIED: no public-SDK workflow construct ends the stream with no output, no pending request, and a non-terminal status; see comment.")]
    public async Task StallWithoutOutputOrPending_Throws()
    {
        // NOT VERIFIED by execution: NativeWorkflowInteraction throws
        // InvalidOperationException("Native workflow stream halted without a pending
        // request or successful completion.") only when the stream ends with no
        // pending request, no WorkflowOutputEvent, and a non-terminal RunStatus
        // (neither Ended nor Idle, and not output-covered). That state is not
        // reachable through the verified public surface (WorkflowBuilder,
        // FunctionExecutor, AddEdge, AddExternalCall, WithOutputFrom,
        // WithIntermediateOutputFrom): runs that finish without external calls end
        // Ended (or Idle), both treated as successful completion, and every
        // PendingRequests halt is accompanied by a RequestInfoEvent that arms
        // PendingExternalRequest. Forcing a construct here would require inventing
        // or reflecting over non-public runtime state, so no executable assertion
        // is provided instead of a brittle one.
        await Task.CompletedTask;
    }

    private static Workflow CreateOutputThenHaltWorkflow()
    {
        var source = new FunctionExecutor<string, string>(
            "output-source",
            (input, _, _) => ValueTask.FromResult(input));
        var gate = new FunctionExecutor<string, object>(
            "request-gate",
            (input, _, _) => ValueTask.FromResult<object>(
                input == "start" ? (object)new ApprovalRequest(input) : (object)("done:" + input)));
        return new WorkflowBuilder(source)
            .AddEdge(source, gate)
            .AddExternalCall<ApprovalRequest, string>(gate, "approval")
            .WithIntermediateOutputFrom([source])
            .WithOutputFrom(gate)
            .Build();
    }

    private static Workflow CreateWorkflow()
    {
        var source = new FunctionExecutor<string, ApprovalRequest>(
            "request-source",
            (input, _, _) => ValueTask.FromResult(new ApprovalRequest(input)));
        return new WorkflowBuilder(source)
            .AddExternalCall<ApprovalRequest, string>(source, "approval")
            .WithOutputFrom(source)
            .Build();
    }

    private static Workflow CreatePassthroughWorkflow()
    {
        var source = new FunctionExecutor<string, string>(
            "passthrough",
            (input, _, _) => ValueTask.FromResult(input));
        return new WorkflowBuilder(source)
            .WithOutputFrom(source)
            .Build();
    }

    private static async Task<bool> CaptureAsync(Task task)
    {
        try
        {
            await task;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private sealed record ApprovalRequest(string Text);
}
