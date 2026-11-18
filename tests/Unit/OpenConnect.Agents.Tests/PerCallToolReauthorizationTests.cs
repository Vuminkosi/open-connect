// <copyright file="PerCallToolReauthorizationTests.cs">
// Tests for per-call tool reauthorization against current grants (OC2-D-AUTH-003).
// Verifies every consequential tool call reauthorizes against current grants, deadline,
// revocation state, and remaining budget, failing closed before invocation.
// </copyright>

using System.Collections.Immutable;
using Microsoft.Extensions.AI;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class PerCallToolReauthorizationTests
{
    private static readonly ToolId WriteTool = new("write_database");

    [Fact]
    public async Task ConsequentialTool_Succeeds_WhenFullyAuthorized()
    {
        var executions = 0;
        var inner = AIFunctionFactory.Create((string query) => { executions++; return "saved"; }, "write_database");

        var context = CreateContext(deadline: DateTimeOffset.UtcNow.AddMinutes(5));
        var activeGrants = new HashSet<ToolId> { WriteTool };

        var guarded = PerCallToolReauthorizer.Wrap(
            inner,
            WriteTool,
            ToolImpact.Write,
            toolId => activeGrants.Contains(toolId),
            (ctx, ct) => ValueTask.FromResult(false),
            context);

        var result = await guarded.InvokeAsync(new AIFunctionArguments { ["query"] = "insert" });

        Assert.Equal("saved", result?.ToString());
        Assert.Equal(1, executions);
    }

    [Fact]
    public async Task ConsequentialTool_FailsClosed_WhenRevokedMidExecution()
    {
        var executions = 0;
        var inner = AIFunctionFactory.Create((string query) => { executions++; return "saved"; }, "write_database");

        var context = CreateContext(deadline: DateTimeOffset.UtcNow.AddMinutes(5));
        var isRevoked = false;

        var guarded = PerCallToolReauthorizer.Wrap(
            inner,
            WriteTool,
            ToolImpact.Write,
            _ => true,
            (ctx, ct) => ValueTask.FromResult(isRevoked),
            context);

        // Turn 1: authorized
        await guarded.InvokeAsync(new AIFunctionArguments { ["query"] = "insert 1" });
        Assert.Equal(1, executions);

        // Grant is revoked before turn 2
        isRevoked = true;

        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
        {
            await guarded.InvokeAsync(new AIFunctionArguments { ["query"] = "insert 2" });
        });

        Assert.Contains("authorization grant has been revoked", ex.Message);
        Assert.Equal(1, executions); // Execution count remains 1; second call NEVER ran!
    }

    [Fact]
    public async Task ConsequentialTool_FailsClosed_WhenDeadlineExpired()
    {
        var executions = 0;
        var inner = AIFunctionFactory.Create(() => { executions++; return "ok"; }, "write_database");

        // Expired deadline
        var context = CreateContext(deadline: DateTimeOffset.UtcNow.AddSeconds(-10));

        var guarded = PerCallToolReauthorizer.Wrap(
            inner,
            WriteTool,
            ToolImpact.Write,
            _ => true,
            (ctx, ct) => ValueTask.FromResult(false),
            context);

        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
        {
            await guarded.InvokeAsync();
        });

        Assert.Contains("deadline has expired", ex.Message);
        Assert.Equal(0, executions); // Zero invocations!
    }

    [Fact]
    public async Task ConsequentialTool_FailsClosed_WhenDroppedFromActiveGrants()
    {
        var executions = 0;
        var inner = AIFunctionFactory.Create(() => { executions++; return "ok"; }, "write_database");

        var context = CreateContext(deadline: DateTimeOffset.UtcNow.AddMinutes(5));
        var activeGrants = new HashSet<ToolId> { WriteTool };

        var guarded = PerCallToolReauthorizer.Wrap(
            inner,
            WriteTool,
            ToolImpact.Write,
            toolId => activeGrants.Contains(toolId),
            (ctx, ct) => ValueTask.FromResult(false),
            context);

        // First call: in active grants
        await guarded.InvokeAsync();
        Assert.Equal(1, executions);

        // Tool grant dropped from current grants mid-execution
        activeGrants.Remove(WriteTool);

        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
        {
            await guarded.InvokeAsync();
        });

        Assert.Contains("not in current active grants", ex.Message);
        Assert.Equal(1, executions); // Underlyng tool was NOT invoked again!
    }

    [Fact]
    public async Task ConsequentialTool_FailsClosed_WhenToolBudgetExhausted()
    {
        var executions = 0;
        var inner = AIFunctionFactory.Create(() => { executions++; return "ok"; }, "write_database");

        var context = CreateContext(deadline: DateTimeOffset.UtcNow.AddMinutes(5));
        var ledger = new RunBudgetLedger(new AgentBudget(MaxToolCalls: 1)); // Only 1 tool call allowed

        var guarded = PerCallToolReauthorizer.Wrap(
            inner,
            WriteTool,
            ToolImpact.Write,
            _ => true,
            (ctx, ct) => ValueTask.FromResult(false),
            context,
            ledger);

        // First call reserves the only available slot
        await guarded.InvokeAsync();
        Assert.Equal(1, executions);

        // Second call exhausts budget and fails closed
        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
        {
            await guarded.InvokeAsync();
        });

        Assert.Contains("tool call budget exhausted", ex.Message);
        Assert.Equal(1, executions);
    }

    private static AgentExecutionContext CreateContext(DateTimeOffset deadline)
    {
        return new AgentExecutionContext
        {
            TenantScope = "tenant-1",
            PrincipalOrWorkloadScope = "user-1",
            IdentityKind = AgentIdentityMode.ActiveUser,
            SessionId = new SessionId(Ulid.NewUlid()),
            TaskId = new TaskId(Ulid.NewUlid()),
            AgentExecutionId = new ExecutionId(Ulid.NewUlid()),
            Depth = 0,
            CatalogRevision = 1,
            PolicyRevision = 1,
            AuthRevision = 1,
            Deadline = deadline,
            RemainingBudgets = ImmutableDictionary<string, long>.Empty,
            CancellationToken = CancellationToken.None
        };
    }
}
