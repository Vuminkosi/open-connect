using Microsoft.Agents.AI.Workflows;
using OpenConnect.Agents.Execution;
using Xunit;

namespace OpenConnect.Agents.Tests;

/// <summary>
/// Lab proving native concurrent workflows on Microsoft.Agents.AI.Workflows 1.27.0.
/// Covers MAF-013: native fan-out/fan-in via WorkflowBuilder (AddFanOutEdge +
/// AddFanInBarrierEdge), typed merge function, branch cancellation, and per-executor
/// state isolation. No custom workflow engine.
/// </summary>
public sealed class NativeConcurrentWorkflowLabTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Fan_out_fan_in_runs_two_typed_branches_and_merges_deterministically()
    {
        var start = new FunctionExecutor<string, string>(
            "lab-conc-start", (input, _, _) => ValueTask.FromResult(input));
        var branchA = new FunctionExecutor<string, string>(
            "lab-conc-a", (input, _, _) => ValueTask.FromResult(input + ":A"));
        var branchB = new FunctionExecutor<string, string>(
            "lab-conc-b", (input, _, _) => ValueTask.FromResult(input + ":B"));

        // Typed merge: accumulates one typed message per branch in the merge
        // executor's own workflow state and yields a single deterministic value
        // (sorted, so barrier arrival order cannot affect the result).
        var merge = new FunctionExecutor<string>(
            "lab-conc-merge",
            async (input, context, cancellationToken) =>
            {
                var parts = await context.ReadStateAsync<List<string>>("parts", cancellationToken)
                    ?? new List<string>();
                parts.Add(input);
                await context.QueueStateUpdateAsync("parts", parts, cancellationToken);
                if (parts.Count == 2)
                {
                    parts.Sort(StringComparer.Ordinal);
                    await context.YieldOutputAsync("merged:" + string.Join("+", parts), cancellationToken);
                }
            },
            null,
            null,
            [typeof(string)]);

        var workflow = new WorkflowBuilder(start)
            .AddFanOutEdge(start, [branchA, branchB])
            .AddFanInBarrierEdge([branchA, branchB], merge)
            .WithOutputFrom(merge)
            .Build();

        var result = await new NativeWorkflowRunner()
            .RunAsync(workflow, "go")
            .WaitAsync(Timeout);

        // Exactly one merged typed output; deterministic regardless of branch arrival order.
        var output = Assert.Single(result.Outputs);
        Assert.Equal("merged:go:A+go:B", output.As<string>());

        // Both branch outputs present in native events with their typed values.
        var completed = result.Events.OfType<ExecutorCompletedEvent>().ToArray();
        Assert.Contains(completed, e => e.ExecutorId == "lab-conc-a" && (string?)e.Data == "go:A");
        Assert.Contains(completed, e => e.ExecutorId == "lab-conc-b" && (string?)e.Data == "go:B");

        // Fan-in barrier streamed both branch messages: merge ran once per branch.
        Assert.Equal(2, completed.Count(e => e.ExecutorId == "lab-conc-merge"));
    }

    [Fact]
    public async Task Cancel_mid_run_throws_and_emits_no_partial_merge()
    {
        long mergeRan = 0;
        var start = new FunctionExecutor<string, string>(
            "lab-cancel-start", (input, _, _) => ValueTask.FromResult(input));
        var slowA = new FunctionExecutor<string, string>(
            "lab-cancel-a",
            async (input, _, cancellationToken) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                return input + ":A";
            });
        var slowB = new FunctionExecutor<string, string>(
            "lab-cancel-b",
            async (input, _, cancellationToken) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                return input + ":B";
            });
        var merge = new FunctionExecutor<string, string>(
            "lab-cancel-merge",
            (input, _, _) =>
            {
                Interlocked.Exchange(ref mergeRan, 1);
                return ValueTask.FromResult("merged:" + input);
            });

        var workflow = new WorkflowBuilder(start)
            .AddFanOutEdge(start, [slowA, slowB])
            .AddFanInBarrierEdge([slowA, slowB], merge)
            .WithOutputFrom(merge)
            .Build();

        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(200));

        // No success result: cancellation surfaces as OperationCanceledException.
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new NativeWorkflowRunner().RunAsync(workflow, "go", cancellation.Token).WaitAsync(Timeout));

        // The barrier never released, so no partial merge was emitted.
        Assert.Equal(0, Interlocked.Read(ref mergeRan));
    }

    [Fact]
    public async Task Branches_keep_scratch_state_isolated_from_each_other()
    {
        var start = new FunctionExecutor<string, string>(
            "lab-iso-start", (input, _, _) => ValueTask.FromResult(input));
        var branchA = new FunctionExecutor<string, string>(
            "lab-iso-a",
            async (input, context, cancellationToken) =>
            {
                await context.QueueStateUpdateAsync("scratch", "A-value", cancellationToken);
                var own = await context.ReadStateAsync<string>("scratch", cancellationToken);
                return $"A:own={own};in={input}";
            });
        var branchB = new FunctionExecutor<string, string>(
            "lab-iso-b",
            async (input, context, cancellationToken) =>
            {
                await context.QueueStateUpdateAsync("scratch", "B-value", cancellationToken);
                var own = await context.ReadStateAsync<string>("scratch", cancellationToken);
                return $"B:own={own};in={input}";
            });
        var merge = new FunctionExecutor<string>(
            "lab-iso-merge",
            async (input, context, cancellationToken) =>
            {
                var parts = await context.ReadStateAsync<List<string>>("parts", cancellationToken)
                    ?? new List<string>();
                parts.Add(input);
                await context.QueueStateUpdateAsync("parts", parts, cancellationToken);
                if (parts.Count == 2)
                {
                    parts.Sort(StringComparer.Ordinal);
                    // The merge executor's own default scope never received either
                    // branch's scratch key: branch state is invisible here.
                    var leaked = await context.ReadStateAsync<string>("scratch", cancellationToken);
                    await context.YieldOutputAsync(
                        $"merged:[{string.Join("|", parts)}];scratch={leaked ?? "<null>"}",
                        cancellationToken);
                }
            },
            null,
            null,
            [typeof(string)]);

        var workflow = new WorkflowBuilder(start)
            .AddFanOutEdge(start, [branchA, branchB])
            .AddFanInBarrierEdge([branchA, branchB], merge)
            .WithOutputFrom(merge)
            .Build();

        var result = await new NativeWorkflowRunner()
            .RunAsync(workflow, "go")
            .WaitAsync(Timeout);

        // Each branch sees only its own scratch value under the same key name.
        var completed = result.Events.OfType<ExecutorCompletedEvent>().ToArray();
        var outputA = Assert.Single(completed, e => e.ExecutorId == "lab-iso-a");
        var outputB = Assert.Single(completed, e => e.ExecutorId == "lab-iso-b");
        Assert.Equal("A:own=A-value;in=go", (string?)outputA.Data);
        Assert.Equal("B:own=B-value;in=go", (string?)outputB.Data);

        // No cross-branch mutation: branch payloads carry distinct values.
        Assert.DoesNotContain("B-value", (string)outputA.Data!);
        Assert.DoesNotContain("A-value", (string)outputB.Data!);

        // Branch scratch state did not leak into the merge executor's scope.
        var merged = Assert.Single(result.Outputs);
        var mergedText = merged.As<string>();
        Assert.Contains("A:own=A-value;in=go", mergedText);
        Assert.Contains("B:own=B-value;in=go", mergedText);
        Assert.Contains("scratch=<null>", mergedText);
    }
}
