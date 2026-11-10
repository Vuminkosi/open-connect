using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Xunit;

namespace OpenConnect.Agents.Tests;

/// <summary>
/// Lab proving the combined native HITL pause + checkpoint persistence/restore on
/// Microsoft.Agents.AI.Workflows 1.27.0 (covers OC2-D-MAF-015 a/b/c + MAF-016 restart smoke).
/// A checkpointed workflow (<c>WithCheckpointing</c> + <c>CheckpointManager.CreateJson</c>)
/// halts on a native external call (<c>AddExternalCall</c> + FunctionExecutor gate); the
/// halted checkpoint is retrievable and rehydratable; an authorized response completes the
/// run through a downstream counting executor; a denied response is rejected before any
/// response is created or sent (deny-before-send, mirroring
/// <c>NativeWorkflowInteraction</c>); and a fresh run resumed from the rehydrated
/// checkpoint re-emits the same pending request with no downstream progress.
/// Deterministic stubs only, no network. No custom workflow/HITL/checkpoint engine.
/// </summary>
public sealed class NativeHitlCheckpointLabTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Checkpointed_workflow_halts_with_retrievable_checkpoint_while_unapproved()
    {
        const string sessionId = "native-hitl-checkpoint-halt";
        using var timeout = new CancellationTokenSource(Timeout);
        var store = new JsonCheckpointStoreForTest();
        var manager = CheckpointManager.CreateJson(store);
        var counter = new Counter();
        var workflow = CreateWorkflow(counter);

        var environment = InProcessExecution.Default.WithCheckpointing(manager);
        await using var run = await environment.RunStreamingAsync(workflow, "start", sessionId, timeout.Token);

        var (pending, outputs) = await DrainAsync(run, timeout.Token);
        Assert.NotNull(pending);
        Assert.Equal("approval", pending!.PortInfo.PortId);
        Assert.True(pending.TryGetDataAs<ApprovalRequest>(out var payload));
        Assert.Equal("start", payload!.Text);
        Assert.Empty(outputs);
        Assert.Equal(RunStatus.PendingRequests, await run.GetStatusAsync(timeout.Token));

        var checkpoint = await manager.GetLatestCheckpointAsync(sessionId, timeout.Token);
        Assert.NotNull(checkpoint);
        Assert.NotEmpty(store.SerializedCheckpoints);

        await run.CancelRunAsync();
    }

    [Fact]
    public async Task Authorized_response_completes_run_through_downstream_executor()
    {
        const string sessionId = "native-hitl-checkpoint-approved";
        using var timeout = new CancellationTokenSource(Timeout);
        var store = new JsonCheckpointStoreForTest();
        var manager = CheckpointManager.CreateJson(store);
        var counter = new Counter();
        var workflow = CreateWorkflow(counter);

        var environment = InProcessExecution.Default.WithCheckpointing(manager);
        await using var run = await environment.RunStreamingAsync(workflow, "start", sessionId, timeout.Token);

        var (pending, _) = await DrainAsync(run, timeout.Token);
        Assert.NotNull(pending);

        await run.SendResponseAsync(pending!.CreateResponse("approved"));
        var (_, outputs) = await DrainAsync(run, timeout.Token);

        var status = await run.GetStatusAsync(timeout.Token);
        Assert.True(
            status is RunStatus.Ended or RunStatus.Idle,
            $"Unexpected terminal status {status}; expected Ended or Idle.");
        Assert.Equal(1, Volatile.Read(ref counter.Value));
        Assert.Equal("completed:approved", Assert.Single(outputs));
    }

    [Fact]
    public async Task Denied_response_is_rejected_before_send_and_run_stays_pending()
    {
        const string sessionId = "native-hitl-checkpoint-denied";
        using var timeout = new CancellationTokenSource(Timeout);
        var store = new JsonCheckpointStoreForTest();
        var manager = CheckpointManager.CreateJson(store);
        var counter = new Counter();
        var workflow = CreateWorkflow(counter);

        var environment = InProcessExecution.Default.WithCheckpointing(manager);
        await using var run = await environment.RunStreamingAsync(workflow, "start", sessionId, timeout.Token);

        var (pending, outputs) = await DrainAsync(run, timeout.Token);
        Assert.NotNull(pending);
        Assert.Empty(outputs);

        // Deny-before-send (mirrors NativeWorkflowInteraction.cs): authorize runs before
        // any response is created or sent; denial throws before CreateResponse/SendResponseAsync.
        Func<ExternalRequest, CancellationToken, ValueTask<bool>> authorize = DenyAsync;
        var denial = await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
        {
            if (!await authorize(pending!, timeout.Token).ConfigureAwait(false))
            {
                throw new UnauthorizedAccessException("The host did not authorize this workflow response.");
            }

            await run.SendResponseAsync(pending!.CreateResponse("approved"));
        });
        Assert.Equal("The host did not authorize this workflow response.", denial.Message);

        Assert.Equal(0, Volatile.Read(ref counter.Value));
        Assert.Equal(RunStatus.PendingRequests, await run.GetStatusAsync(timeout.Token));

        await run.CancelRunAsync();
    }

    [Fact]
    public async Task Resumed_run_reemits_pending_request_without_downstream_progress()
    {
        const string sessionId = "native-hitl-checkpoint-resume";
        using var timeout = new CancellationTokenSource(Timeout);
        var store = new JsonCheckpointStoreForTest();
        var manager = CheckpointManager.CreateJson(store);
        var counter = new Counter();
        var workflow = CreateWorkflow(counter);

        var environment = InProcessExecution.Default.WithCheckpointing(manager);
        await using var run = await environment.RunStreamingAsync(workflow, "start", sessionId, timeout.Token);

        var (pending, _) = await DrainAsync(run, timeout.Token);
        Assert.NotNull(pending);

        var checkpoint = await manager.GetLatestCheckpointAsync(sessionId, timeout.Token);
        Assert.NotNull(checkpoint);

        await run.CancelRunAsync();

        // Rehydrate only the native JSON checkpoint objects into fresh test infrastructure.
        var freshStore = JsonCheckpointStoreForTest.FromSerialized(store.SerializedCheckpoints);
        var freshManager = CheckpointManager.CreateJson(freshStore);
        var restored = await freshManager.GetLatestCheckpointAsync(sessionId, timeout.Token);
        Assert.NotNull(restored);
        Assert.Equal(checkpoint!.CheckpointId, restored!.CheckpointId);

        // A Workflow instance is owned by its first runner: resume with a freshly
        // built instance of the same definition (shared counter still proves no
        // downstream progress on the resumed run).
        var resumedWorkflow = CreateWorkflow(counter);
        await using var resumed = await InProcessExecution.ResumeStreamingAsync(
            resumedWorkflow, restored, freshManager, timeout.Token);
        var (resumedPending, resumedOutputs) = await DrainAsync(resumed, timeout.Token);

        Assert.NotNull(resumedPending);
        Assert.Equal(pending!.PortInfo.PortId, resumedPending!.PortInfo.PortId);
        Assert.True(resumedPending.TryGetDataAs<ApprovalRequest>(out var resumedPayload));
        Assert.Equal("start", resumedPayload!.Text);
        Assert.Empty(resumedOutputs);
        Assert.Equal(0, Volatile.Read(ref counter.Value));

        await resumed.CancelRunAsync();
    }

    private static Workflow CreateWorkflow(Counter counter)
    {
        var gate = new FunctionExecutor<string, object>(
            "request-gate",
            (input, _context, _cancellationToken) => ValueTask.FromResult<object>(
                input == "start" ? (object)new ApprovalRequest(input) : (object)("completed:" + input)));
        var downstream = new FunctionExecutor<string, string>(
            "downstream",
            (input, _context, _cancellationToken) =>
            {
                Interlocked.Increment(ref counter.Value);
                return ValueTask.FromResult(input);
            });
        return new WorkflowBuilder(gate)
            .AddExternalCall<ApprovalRequest, string>(gate, "approval")
            .AddEdge(gate, downstream)
            .WithOutputFrom(downstream)
            .Build();
    }

    private static async Task<(ExternalRequest? Pending, List<string> Outputs)> DrainAsync(
        StreamingRun run, CancellationToken cancellationToken)
    {
        ExternalRequest? pending = null;
        var outputs = new List<string>();
        await foreach (var workflowEvent in run.WatchStreamAsync(
            blockOnPendingRequest: false, cancellationToken).ConfigureAwait(false))
        {
            if (workflowEvent is RequestInfoEvent requestInfo)
            {
                pending = requestInfo.Request;
            }
            else if (workflowEvent is WorkflowOutputEvent output)
            {
                var text = output.As<string>();
                Assert.NotNull(text);
                outputs.Add(text!);
            }
        }

        return (pending, outputs);
    }

    private static ValueTask<bool> DenyAsync(ExternalRequest request, CancellationToken cancellationToken)
        => ValueTask.FromResult(false);

    private sealed class Counter
    {
        public int Value;
    }

    private sealed record ApprovalRequest(string Text);

    private sealed class JsonCheckpointStoreForTest : ICheckpointStore<JsonElement>
    {
        private readonly List<StoredCheckpoint> _checkpoints = [];

        public IReadOnlyList<StoredCheckpoint> SerializedCheckpoints => _checkpoints;

        public ValueTask<IEnumerable<CheckpointInfo>> RetrieveIndexAsync(
            string sessionId,
            CheckpointInfo? withParent = null)
        {
            IEnumerable<CheckpointInfo> index = _checkpoints
                .Where(checkpoint => checkpoint.SessionId == sessionId)
                .Where(checkpoint => withParent is null || checkpoint.ParentCheckpointId == withParent.CheckpointId)
                .Select(checkpoint => checkpoint.Info)
                .ToArray();
            return ValueTask.FromResult(index);
        }

        public ValueTask<CheckpointInfo> CreateCheckpointAsync(
            string sessionId,
            JsonElement value,
            CheckpointInfo? parent)
        {
            var info = new CheckpointInfo(sessionId, Guid.NewGuid().ToString("N"));
            _checkpoints.Add(new StoredCheckpoint(sessionId, value.Clone(), info, parent?.CheckpointId));
            return ValueTask.FromResult(info);
        }

        public ValueTask<JsonElement> RetrieveCheckpointAsync(string sessionId, CheckpointInfo key)
        {
            var checkpoint = _checkpoints.LastOrDefault(item =>
                item.SessionId == sessionId && item.Info.CheckpointId == key.CheckpointId);
            return checkpoint is null
                ? ValueTask.FromException<JsonElement>(new KeyNotFoundException("Checkpoint not found."))
                : ValueTask.FromResult(checkpoint.Value.Clone());
        }

        public static JsonCheckpointStoreForTest FromSerialized(IEnumerable<StoredCheckpoint> checkpoints)
        {
            var store = new JsonCheckpointStoreForTest();
            store._checkpoints.AddRange(checkpoints.Select(checkpoint => checkpoint with
            {
                Value = checkpoint.Value.Clone()
            }));
            return store;
        }

        public sealed record StoredCheckpoint(string SessionId, JsonElement Value, CheckpointInfo Info,
            string? ParentCheckpointId);
    }
}
