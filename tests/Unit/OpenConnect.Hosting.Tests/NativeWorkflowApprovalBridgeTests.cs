using Microsoft.Agents.AI.Workflows;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using OpenConnect.Hosting.AspNetCore.Execution;
using Xunit;

namespace OpenConnect.Hosting.Tests;

/// <summary>Contract tests for the in-process native workflow approval bridge (WF-HITL-02 / WF-03).</summary>
public sealed class NativeWorkflowApprovalBridgeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private const string ExpectedArgs = "args-hash-1";
    private const string ExpectedToolRev = "tool-rev-7";

    [Fact]
    public async Task ApprovedOnce_ContinuesWorkflow()
    {
        await using var interaction = await NativeWorkflowInteraction.StartAsync(CreateWorkflow(), "start")
            .WaitAsync(Timeout);
        var scope = NewScope();
        var request = Assert.IsType<ExternalRequest>(interaction.PendingExternalRequest);
        var bridge = CreateBridge(interaction, scope);

        await bridge.ApproveAsync(request, CreateDecision(scope, request, "n1"), "approved")
            .WaitAsync(Timeout);

        var second = Assert.IsType<ExternalRequest>(interaction.PendingExternalRequest);
        Assert.NotSame(request, second);

        await interaction.CancelAsync().WaitAsync(Timeout);
    }

    [Fact]
    public async Task DeniedApproval_DoesNotContinue()
    {
        await using var interaction = await NativeWorkflowInteraction.StartAsync(CreateWorkflow(), "start")
            .WaitAsync(Timeout);
        var scope = NewScope();
        var request = Assert.IsType<ExternalRequest>(interaction.PendingExternalRequest);
        var bridge = CreateBridge(interaction, scope);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            bridge.ApproveAsync(request, CreateDecision(scope, request, "n1", approved: false), "approved")
                .WaitAsync(Timeout));

        Assert.Same(request, interaction.PendingExternalRequest);
    }

    [Fact]
    public async Task ExpiredApproval_Rejected()
    {
        await using var interaction = await NativeWorkflowInteraction.StartAsync(CreateWorkflow(), "start")
            .WaitAsync(Timeout);
        var scope = NewScope();
        var request = Assert.IsType<ExternalRequest>(interaction.PendingExternalRequest);
        var bridge = CreateBridge(interaction, scope);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            bridge.ApproveAsync(request,
                    CreateDecision(scope, request, "n1", expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1)),
                    "approved")
                .WaitAsync(Timeout));

        Assert.Same(request, interaction.PendingExternalRequest);
    }

    [Fact]
    public async Task RevokedApproval_Rejected()
    {
        await using var interaction = await NativeWorkflowInteraction.StartAsync(CreateWorkflow(), "start")
            .WaitAsync(Timeout);
        var scope = NewScope();
        var request = Assert.IsType<ExternalRequest>(interaction.PendingExternalRequest);
        var bridge = CreateBridge(interaction, scope,
            revoked: _ => ValueTask.FromResult(true));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            bridge.ApproveAsync(request, CreateDecision(scope, request, "n1"), "approved")
                .WaitAsync(Timeout));

        Assert.Same(request, interaction.PendingExternalRequest);
    }

    [Fact]
    public async Task WithdrawnGrant_Rejected()
    {
        await using var interaction = await NativeWorkflowInteraction.StartAsync(CreateWorkflow(), "start")
            .WaitAsync(Timeout);
        var scope = NewScope();
        var request = Assert.IsType<ExternalRequest>(interaction.PendingExternalRequest);
        var bridge = CreateBridge(interaction, scope,
            grantValid: _ => ValueTask.FromResult(false));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            bridge.ApproveAsync(request, CreateDecision(scope, request, "n1"), "approved")
                .WaitAsync(Timeout));

        Assert.Same(request, interaction.PendingExternalRequest);
    }

    [Fact]
    public async Task ArgumentsMismatch_Rejected()
    {
        await using var interaction = await NativeWorkflowInteraction.StartAsync(CreateWorkflow(), "start")
            .WaitAsync(Timeout);
        var scope = NewScope();
        var request = Assert.IsType<ExternalRequest>(interaction.PendingExternalRequest);
        var bridge = CreateBridge(interaction, scope);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            bridge.ApproveAsync(request, CreateDecision(scope, request, "n1", args: "tampered-hash"), "approved")
                .WaitAsync(Timeout));

        Assert.Same(request, interaction.PendingExternalRequest);
    }

    [Fact]
    public async Task ToolRevisionMismatch_Rejected()
    {
        await using var interaction = await NativeWorkflowInteraction.StartAsync(CreateWorkflow(), "start")
            .WaitAsync(Timeout);
        var scope = NewScope();
        var request = Assert.IsType<ExternalRequest>(interaction.PendingExternalRequest);
        var bridge = CreateBridge(interaction, scope);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            bridge.ApproveAsync(request, CreateDecision(scope, request, "n1", rev: "tool-rev-999"), "approved")
                .WaitAsync(Timeout));

        Assert.Same(request, interaction.PendingExternalRequest);
    }

    [Theory]
    [InlineData(0)] // tenant
    [InlineData(1)] // principal (actor)
    [InlineData(2)] // session
    [InlineData(3)] // task
    public async Task ScopeMismatch_Rejected(int which)
    {
        await using var interaction = await NativeWorkflowInteraction.StartAsync(CreateWorkflow(), "start")
            .WaitAsync(Timeout);
        var scope = NewScope();
        var request = Assert.IsType<ExternalRequest>(interaction.PendingExternalRequest);
        var bridge = CreateBridge(interaction, scope);
        var presented = which switch
        {
            0 => scope with { TenantId = new TenantId(Ulid.NewUlid()) },
            1 => scope with { PrincipalId = new PrincipalId(Ulid.NewUlid()) },
            2 => scope with { SessionId = new SessionId(Ulid.NewUlid()) },
            _ => scope with { TaskId = new TaskId(Ulid.NewUlid()) },
        };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            bridge.ApproveAsync(request, CreateDecision(presented, request, "n1"), "approved")
                .WaitAsync(Timeout));

        Assert.Same(request, interaction.PendingExternalRequest);
    }

    [Fact]
    public async Task StaleRequest_RejectedWithoutContinuation()
    {
        await using var interaction = await NativeWorkflowInteraction.StartAsync(CreateWorkflow(), "start")
            .WaitAsync(Timeout);
        await using var other = await NativeWorkflowInteraction.StartAsync(CreateWorkflow(), "start")
            .WaitAsync(Timeout);
        var scope = NewScope();
        var request = Assert.IsType<ExternalRequest>(interaction.PendingExternalRequest);
        var foreign = Assert.IsType<ExternalRequest>(other.PendingExternalRequest);
        var bridge = CreateBridge(interaction, scope);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            bridge.ApproveAsync(foreign, CreateDecision(scope, foreign, "n1"), "approved")
                .WaitAsync(Timeout));

        Assert.Same(request, interaction.PendingExternalRequest);
        Assert.Same(foreign, other.PendingExternalRequest);
    }

    [Fact]
    public async Task ReplayedRequest_AfterApproval_Rejected()
    {
        await using var interaction = await NativeWorkflowInteraction.StartAsync(CreateWorkflow(), "start")
            .WaitAsync(Timeout);
        var scope = NewScope();
        var request = Assert.IsType<ExternalRequest>(interaction.PendingExternalRequest);
        var bridge = CreateBridge(interaction, scope);
        var decision = CreateDecision(scope, request, "n1");
        await bridge.ApproveAsync(request, decision, "approved").WaitAsync(Timeout);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            bridge.ApproveAsync(request, decision, "approved").WaitAsync(Timeout));

        await interaction.CancelAsync().WaitAsync(Timeout);
    }

    [Fact]
    public async Task ReusedNonce_OnNextRequest_Rejected()
    {
        await using var interaction = await NativeWorkflowInteraction.StartAsync(CreateWorkflow(), "start")
            .WaitAsync(Timeout);
        var scope = NewScope();
        var request = Assert.IsType<ExternalRequest>(interaction.PendingExternalRequest);
        var bridge = CreateBridge(interaction, scope);
        await bridge.ApproveAsync(request, CreateDecision(scope, request, "n1"), "approved")
            .WaitAsync(Timeout);

        var second = Assert.IsType<ExternalRequest>(interaction.PendingExternalRequest);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            bridge.ApproveAsync(second, CreateDecision(scope, second, "n1"), "approved-again")
                .WaitAsync(Timeout));

        Assert.Same(second, interaction.PendingExternalRequest);
        await interaction.CancelAsync().WaitAsync(Timeout);
    }

    [Fact]
    public async Task ConcurrentApprovals_AcceptAtMostOneContinuation()
    {
        await using var interaction = await NativeWorkflowInteraction.StartAsync(CreateWorkflow(), "start")
            .WaitAsync(Timeout);
        var scope = NewScope();
        var request = Assert.IsType<ExternalRequest>(interaction.PendingExternalRequest);
        var bridge = CreateBridge(interaction, scope);

        var outcomes = await Task.WhenAll(
                CaptureAsync(() => bridge.ApproveAsync(request, CreateDecision(scope, request, "c1"), "first")),
                CaptureAsync(() => bridge.ApproveAsync(request, CreateDecision(scope, request, "c2"), "second")))
            .WaitAsync(Timeout);

        Assert.Equal(2, outcomes.Length);
        Assert.Single(outcomes, o => o is null);
        var loser = Assert.Single(outcomes, o => o is not null);
        Assert.True(
            loser is InvalidOperationException || loser is UnauthorizedAccessException,
            $"Loser must fail closed, observed: {loser?.GetType().FullName}");
        await interaction.CancelAsync().WaitAsync(Timeout);
    }

    [Fact]
    public async Task ConcurrentSameNonce_AcceptsAtMostOneContinuation()
    {
        await using var interaction = await NativeWorkflowInteraction.StartAsync(CreateWorkflow(), "start")
            .WaitAsync(Timeout);
        var scope = NewScope();
        var request = Assert.IsType<ExternalRequest>(interaction.PendingExternalRequest);
        var bridge = CreateBridge(interaction, scope);

        var outcomes = await Task.WhenAll(
                CaptureAsync(() => bridge.ApproveAsync(request, CreateDecision(scope, request, "n-same"), "first")),
                CaptureAsync(() => bridge.ApproveAsync(request, CreateDecision(scope, request, "n-same"), "second")))
            .WaitAsync(Timeout);

        Assert.Equal(2, outcomes.Length);
        Assert.Single(outcomes, o => o is null);
        var loser = Assert.Single(outcomes, o => o is not null);
        Assert.True(
            loser is InvalidOperationException || loser is UnauthorizedAccessException,
            $"Loser must fail closed, observed: {loser?.GetType().FullName}");

        var advanced = Assert.IsType<ExternalRequest>(interaction.PendingExternalRequest);
        Assert.NotSame(request, advanced);

        await interaction.CancelAsync().WaitAsync(Timeout);
    }

    [Fact]
    public async Task CancelledInteraction_ApproveFailsClosed()
    {
        await using var interaction = await NativeWorkflowInteraction.StartAsync(CreateWorkflow(), "start")
            .WaitAsync(Timeout);
        var scope = NewScope();
        var request = Assert.IsType<ExternalRequest>(interaction.PendingExternalRequest);
        var bridge = CreateBridge(interaction, scope);

        await interaction.CancelAsync().WaitAsync(Timeout);
        Assert.Null(interaction.PendingExternalRequest);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            bridge.ApproveAsync(request, CreateDecision(scope, request, "n1"), "approved")
                .WaitAsync(Timeout));
    }

    [Fact]
    public async Task CompletedWorkflow_ApproveRejected()
    {
        await using var completed = await NativeWorkflowInteraction.StartAsync(CreatePassthroughWorkflow(), "start")
            .WaitAsync(Timeout);
        await using var other = await NativeWorkflowInteraction.StartAsync(CreateWorkflow(), "start")
            .WaitAsync(Timeout);
        Assert.Null(completed.PendingExternalRequest);
        var scope = NewScope();
        var foreign = Assert.IsType<ExternalRequest>(other.PendingExternalRequest);
        var bridge = CreateBridge(completed, scope);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            bridge.ApproveAsync(foreign, CreateDecision(scope, foreign, "n1"), "approved")
                .WaitAsync(Timeout));

        Assert.Null(completed.PendingExternalRequest);
    }

    [Fact]
    public async Task Constructor_RejectsMissingExpectations()
    {
        await using var interaction = await NativeWorkflowInteraction.StartAsync(CreateWorkflow(), "start")
            .WaitAsync(Timeout);
        Assert.Throws<ArgumentException>(() =>
            new NativeWorkflowApprovalBridge(interaction, NewScope(), "", ExpectedToolRev));
        Assert.Throws<ArgumentException>(() =>
            new NativeWorkflowApprovalBridge(interaction, NewScope(), ExpectedArgs, "  "));
        await interaction.CancelAsync().WaitAsync(Timeout);
    }

    [Fact]
    public async Task ExpiryFlip_BetweenPrecheckAndAuthorize_DeniesWithoutBurningNonce()
    {
        var valid = DateTimeOffset.UtcNow;
        var expiresAt = valid.AddMinutes(5);
        var late = expiresAt.AddMinutes(1);
        // Reads: precheck valid, authorize expired, retry precheck valid, retry authorize valid.
        var clock = new ScriptedTimeProvider([valid, late, valid, valid]);
        await using var interaction = await NativeWorkflowInteraction.StartAsync(CreateWorkflow(), "start")
            .WaitAsync(Timeout);
        var scope = NewScope();
        var request = Assert.IsType<ExternalRequest>(interaction.PendingExternalRequest);
        var bridge = CreateBridge(interaction, scope, timeProvider: clock);
        var decision = CreateDecision(scope, request, "n-expiry-flip", expiresAt: expiresAt);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            bridge.ApproveAsync(request, decision, "approved")
                .WaitAsync(Timeout));

        Assert.Same(request, interaction.PendingExternalRequest);

        // The denied attempt burned no nonce: the same nonce succeeds once the clock reads valid.
        await bridge.ApproveAsync(request, decision, "approved")
            .WaitAsync(Timeout);

        var second = Assert.IsType<ExternalRequest>(interaction.PendingExternalRequest);
        Assert.NotSame(request, second);

        await interaction.CancelAsync().WaitAsync(Timeout);
    }

    [Fact]
    public async Task RevocationLanding_BeforeAuthorize_DeniesWithoutBurningNonce()
    {
        var now = DateTimeOffset.UtcNow;
        var revoked = false;
        // Land the revocation between the precheck read (index 0) and the authorize read (index 1).
        var clock = new ScriptedTimeProvider([now, now, now, now], readIndex =>
        {
            if (readIndex == 1)
            {
                revoked = true;
            }
        });
        await using var interaction = await NativeWorkflowInteraction.StartAsync(CreateWorkflow(), "start")
            .WaitAsync(Timeout);
        var scope = NewScope();
        var request = Assert.IsType<ExternalRequest>(interaction.PendingExternalRequest);
        var bridge = CreateBridge(interaction, scope,
            revoked: _ => ValueTask.FromResult(revoked),
            timeProvider: clock);
        var decision = CreateDecision(scope, request, "n-revoke-flip", expiresAt: now.AddMinutes(5));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            bridge.ApproveAsync(request, decision, "approved")
                .WaitAsync(Timeout));

        Assert.Same(request, interaction.PendingExternalRequest);

        // The denied attempt burned no nonce: clearing the revocation lets the same nonce succeed.
        revoked = false;
        await bridge.ApproveAsync(request, decision, "approved")
            .WaitAsync(Timeout);

        var second = Assert.IsType<ExternalRequest>(interaction.PendingExternalRequest);
        Assert.NotSame(request, second);

        await interaction.CancelAsync().WaitAsync(Timeout);
    }

    [Fact]
    public async Task RequestIdMismatch_Rejected()
    {
        await using var interaction = await NativeWorkflowInteraction.StartAsync(CreateWorkflow(), "start")
            .WaitAsync(Timeout);
        var scope = NewScope();
        var request = Assert.IsType<ExternalRequest>(interaction.PendingExternalRequest);
        var bridge = CreateBridge(interaction, scope);
        var decision = CreateDecision(scope, request, "n-req-mismatch") with { RequestId = "request-id-wrong" };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            bridge.ApproveAsync(request, decision, "approved")
                .WaitAsync(Timeout));

        Assert.Same(request, interaction.PendingExternalRequest);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmptyNonce_Rejected(string nonce)
    {
        await using var interaction = await NativeWorkflowInteraction.StartAsync(CreateWorkflow(), "start")
            .WaitAsync(Timeout);
        var scope = NewScope();
        var request = Assert.IsType<ExternalRequest>(interaction.PendingExternalRequest);
        var bridge = CreateBridge(interaction, scope);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            bridge.ApproveAsync(request, CreateDecision(scope, request, nonce), "approved")
                .WaitAsync(Timeout));

        Assert.Same(request, interaction.PendingExternalRequest);
    }

    [Fact]
    public async Task DeniedDoesNotBurnNonce_RetrySucceeds()
    {
        await using var interaction = await NativeWorkflowInteraction.StartAsync(CreateWorkflow(), "start")
            .WaitAsync(Timeout);
        var scope = NewScope();
        var request = Assert.IsType<ExternalRequest>(interaction.PendingExternalRequest);
        var bridge = CreateBridge(interaction, scope);
        const string nonce = "n-denied-retry";

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            bridge.ApproveAsync(request, CreateDecision(scope, request, nonce, args: "tampered-hash"), "approved")
                .WaitAsync(Timeout));

        Assert.Same(request, interaction.PendingExternalRequest);

        // The denied attempt burned no nonce: the same nonce succeeds with corrected args.
        await bridge.ApproveAsync(request, CreateDecision(scope, request, nonce), "approved")
            .WaitAsync(Timeout);

        var second = Assert.IsType<ExternalRequest>(interaction.PendingExternalRequest);
        Assert.NotSame(request, second);

        await interaction.CancelAsync().WaitAsync(Timeout);
    }

    private static NativeWorkflowApprovalScope NewScope() => new(
        new TenantId(Ulid.NewUlid()),
        new PrincipalId(Ulid.NewUlid()),
        new SessionId(Ulid.NewUlid()),
        new TaskId(Ulid.NewUlid()));

    private static NativeWorkflowApprovalBridge CreateBridge(
        NativeWorkflowInteraction interaction,
        NativeWorkflowApprovalScope scope,
        Func<CancellationToken, ValueTask<bool>>? revoked = null,
        Func<CancellationToken, ValueTask<bool>>? grantValid = null,
        TimeProvider? timeProvider = null) =>
        new(interaction, scope, ExpectedArgs, ExpectedToolRev, revoked, grantValid, timeProvider);

    private static NativeWorkflowApprovalDecision CreateDecision(
        NativeWorkflowApprovalScope scope,
        ExternalRequest request,
        string nonce,
        bool approved = true,
        string args = ExpectedArgs,
        string rev = ExpectedToolRev,
        DateTimeOffset? expiresAt = null) =>
        new(approved, scope.TenantId, scope.PrincipalId, scope.SessionId, scope.TaskId,
            request.RequestId, args, rev, nonce,
            expiresAt ?? DateTimeOffset.UtcNow.AddMinutes(5));

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

    private static async Task<Exception?> CaptureAsync(Func<Task> factory)
    {
        try
        {
            await factory();
            return null;
        }
        catch (InvalidOperationException ex)
        {
            return ex;
        }
        catch (UnauthorizedAccessException ex)
        {
            return ex;
        }
    }

    private sealed record ApprovalRequest(string Text);

    private sealed class ScriptedTimeProvider(IReadOnlyList<DateTimeOffset> moments, Action<int>? onRead = null) : TimeProvider
    {
        private int _reads;
        public override DateTimeOffset GetUtcNow()
        {
            var index = _reads++;
            onRead?.Invoke(index);
            return index < moments.Count ? moments[index] : moments[moments.Count - 1];
        }
    }
}
