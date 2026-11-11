using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using OpenConnect.Abstractions;
using OpenConnect.Hosting.AspNetCore;
using OpenConnect.Hosting.AspNetCore.Execution;
using Xunit;

namespace OpenConnect.Hosting.Tests;

public sealed class DurableToolExecutionTrackerTests
{
    private static readonly TaskId SampleTaskId = new(Ulid.NewUlid());
    private static readonly SessionId SampleSessionId = new(Ulid.NewUlid());
    private static readonly TenantId SampleTenantId = new(Ulid.NewUlid());
    private static readonly PrincipalId SamplePrincipalId = new(Ulid.NewUlid());

    [Fact]
    public void AcceptInvocation_CreatesRowWithStableIdAndCanonicalHash()
    {
        var tracker = new DurableToolExecutionTracker();
        var record = tracker.AcceptInvocation(
            SampleTaskId,
            SampleSessionId,
            SampleTenantId,
            SamplePrincipalId,
            toolId: "database.query",
            toolRevision: "v1.2",
            executionMode: ToolExecutionMode.BackgroundDurable,
            canonicalArgumentsJson: "{\"query\":\"SELECT 1\"}",
            requiresApproval: false);

        Assert.NotEqual(default(Ulid), record.InvocationId);
        Assert.Equal(ToolInvocationStatus.Accepted, record.Status);
        Assert.NotEmpty(record.CanonicalArgumentHash);
        Assert.Equal(0, record.CompletedUnits);
        Assert.Null(record.TotalUnits);
    }

    [Fact]
    public void ApprovalGatedTool_PersistsPendingApprovalAndRefusesStartBeforeApprove()
    {
        var tracker = new DurableToolExecutionTracker();
        var record = tracker.AcceptInvocation(
            SampleTaskId,
            SampleSessionId,
            SampleTenantId,
            SamplePrincipalId,
            toolId: "filesystem.delete",
            toolRevision: "v1.0",
            executionMode: ToolExecutionMode.BackgroundDurable,
            canonicalArgumentsJson: "{\"path\":\"/tmp/data\"}",
            requiresApproval: true);

        Assert.Equal(ToolInvocationStatus.PendingApproval, record.Status);

        // Attempting to start before approval must fail
        Assert.Throws<InvalidOperationException>(() => tracker.MarkStarted(record.InvocationId));

        // After approval, transitions to Accepted and can then be started
        var approved = tracker.Approve(record.InvocationId);
        Assert.Equal(ToolInvocationStatus.Accepted, approved.Status);

        var started = tracker.MarkStarted(record.InvocationId);
        Assert.Equal(ToolInvocationStatus.Started, started.Status);
    }

    [Fact]
    public void ReportProgress_EnforcesMonotonicUnitsAndMonitorsFinalizing()
    {
        var tracker = new DurableToolExecutionTracker();
        var record = tracker.AcceptInvocation(
            SampleTaskId,
            SampleSessionId,
            SampleTenantId,
            SamplePrincipalId,
            toolId: "data.import",
            toolRevision: "v1.0",
            executionMode: ToolExecutionMode.BackgroundDurable,
            canonicalArgumentsJson: "{}",
            requiresApproval: false);

        tracker.MarkStarted(record.InvocationId);

        // Advance to 50 of 100 units
        var p1 = tracker.ReportProgress(record.InvocationId, phase: "parsing", completedUnits: 50, totalUnits: 100);
        Assert.Equal(ToolInvocationStatus.Progressing, p1.Status);
        Assert.Equal(50, p1.CompletedUnits);

        // Regressing units must throw
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            tracker.ReportProgress(record.InvocationId, phase: "parsing", completedUnits: 40, totalUnits: 100));

        // Exceeding total units must throw
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            tracker.ReportProgress(record.InvocationId, phase: "parsing", completedUnits: 110, totalUnits: 100));

        // Reaching 100% emits Finalizing until terminal commit
        var finalizing = tracker.ReportProgress(record.InvocationId, phase: "validating", completedUnits: 100, totalUnits: 100);
        Assert.Equal(ToolInvocationStatus.Finalizing, finalizing.Status);

        // Terminal completion
        var completed = tracker.MarkCompleted(record.InvocationId);
        Assert.Equal(ToolInvocationStatus.Completed, completed.Status);
    }

    [Fact]
    public void Heartbeat_UpdatesLivenessWithoutAdvancingUnits()
    {
        var clock = new FakeTimeProvider();
        var tracker = new DurableToolExecutionTracker(clock);
        var record = tracker.AcceptInvocation(
            SampleTaskId,
            SampleSessionId,
            SampleTenantId,
            SamplePrincipalId,
            toolId: "sync.external",
            toolRevision: "v1",
            executionMode: ToolExecutionMode.BackgroundDurable,
            canonicalArgumentsJson: "{}",
            requiresApproval: false);

        tracker.MarkStarted(record.InvocationId);
        tracker.ReportProgress(record.InvocationId, phase: "fetching", completedUnits: 10, totalUnits: 100);

        clock.Advance(TimeSpan.FromSeconds(30));
        var heartbeat = tracker.ReportProgress(record.InvocationId, phase: "fetching", completedUnits: 10, totalUnits: 100, isHeartbeat: true);

        Assert.Equal(10, heartbeat.CompletedUnits);
        Assert.Equal(clock.GetUtcNow(), heartbeat.LastHeartbeatAt);
        Assert.Equal(ToolInvocationStatus.Progressing, heartbeat.Status);
    }

    [Fact]
    public void CancellationRace_CompletedInvocationPreservesOutcome()
    {
        var tracker = new DurableToolExecutionTracker();
        var record = tracker.AcceptInvocation(
            SampleTaskId,
            SampleSessionId,
            SampleTenantId,
            SamplePrincipalId,
            toolId: "calc",
            toolRevision: "v1",
            executionMode: ToolExecutionMode.BackgroundDurable,
            canonicalArgumentsJson: "{}",
            requiresApproval: false);

        tracker.MarkStarted(record.InvocationId);
        tracker.MarkCompleted(record.InvocationId);

        // Late cancellation request after completion does not overwrite terminal Completed status
        var cancelled = tracker.RequestCancellation(record.InvocationId);
        Assert.Equal(ToolInvocationStatus.Completed, cancelled.Status);
    }

    [Fact]
    public void GetSanitized_EnforcesTenantAndPrincipalIsolation()
    {
        var tracker = new DurableToolExecutionTracker();
        var record = tracker.AcceptInvocation(
            SampleTaskId,
            SampleSessionId,
            SampleTenantId,
            SamplePrincipalId,
            toolId: "calc",
            toolRevision: "v1",
            executionMode: ToolExecutionMode.BackgroundDurable,
            canonicalArgumentsJson: "{}",
            requiresApproval: false);

        // Own tenant & principal can see record
        Assert.NotNull(tracker.GetSanitized(record.InvocationId, SampleTenantId, SamplePrincipalId));

        // Foreign tenant cannot see record (conceals existence with null)
        Assert.Null(tracker.GetSanitized(record.InvocationId, new TenantId(Ulid.NewUlid()), SamplePrincipalId));

        // Foreign principal cannot see record
        Assert.Null(tracker.GetSanitized(record.InvocationId, SampleTenantId, new PrincipalId(Ulid.NewUlid())));
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }
}
