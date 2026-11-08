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
