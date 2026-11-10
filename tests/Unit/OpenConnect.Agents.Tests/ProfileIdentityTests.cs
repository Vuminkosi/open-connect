using System.Collections.Immutable;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Catalog;
using OpenConnect.Agents.Execution;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class ProfileIdentityTests
{
    [Fact]
    public void Resolve_MapsDirectProfileWithoutWorkflowRuntime() =>
        Assert.Equal(OrchestrationRunnerKey.DirectRunner,
            OrchestrationProfileRouter.Resolve(AgentOrchestrationMode.Direct, true, false, false, false, false));

    [Theory]
    [InlineData(AgentOrchestrationMode.Sequential, OrchestrationRunnerKey.SequentialRunner)]
    [InlineData(AgentOrchestrationMode.Parallel, OrchestrationRunnerKey.ParallelRunner)]
    [InlineData(AgentOrchestrationMode.Handoff, OrchestrationRunnerKey.HandoffRunner)]
    [InlineData(AgentOrchestrationMode.SupervisorDelegation, OrchestrationRunnerKey.SupervisorRunner)]
    public void Resolve_MapsWorkflowModesWhenRuntimeAvailable(AgentOrchestrationMode mode, OrchestrationRunnerKey expected)
    {
        Assert.Equal(expected,
            OrchestrationProfileRouter.Resolve(mode, true, true, true, true, true));
    }

    [Theory]
    [InlineData(AgentOrchestrationMode.Sequential)]
    [InlineData(AgentOrchestrationMode.Parallel)]
    [InlineData(AgentOrchestrationMode.Handoff)]
    [InlineData(AgentOrchestrationMode.SupervisorDelegation)]
    public void AdapterValidation_AllowsWorkflowModesWhenRuntimeAvailable(AgentOrchestrationMode mode)
    {
        var exception = Record.Exception(() =>
            OrchestrationAdapters.ValidateProfile(mode, true));

        Assert.Null(exception);
    }

    [Fact]
    public void AdapterValidation_AllowsDirectWithoutWorkflowRuntime() =>
        OrchestrationAdapters.ValidateProfile(AgentOrchestrationMode.Direct, true);

    [Fact]
    public void Resolve_RejectsUnsupportedPrimitiveWithModeAndPrimitive() =>
        Assert.Contains("Sequential", Assert.Throws<InvalidOperationException>(() =>
            OrchestrationProfileRouter.Resolve(AgentOrchestrationMode.Sequential, true, false, true, true, true)).Message);

    [Fact]
    public void Resolve_RejectsUndefinedMode() =>
        Assert.Throws<InvalidOperationException>(() => OrchestrationProfileRouter.Resolve((AgentOrchestrationMode)99, true, true, true, true, true));

    [Fact]
    public void ValidateRuntimeProfile_AllowsWorkflowWhenRuntimeAvailable()
    {
        var exception = Record.Exception(() =>
            OrchestrationProfileRouter.ValidateRuntimeProfile("Workflow"));

        Assert.Null(exception);
    }

    [Fact]
    public void ValidateRuntimeProfile_RejectsDurableWorkflowWithDurableMissingType()
    {
        var error = Assert.Throws<MafCapabilityUnavailableException>(() =>
            OrchestrationProfileRouter.ValidateRuntimeProfile("DurableWorkflow"));

        Assert.Equal("DurableWorkflow", error.Profile);
        Assert.Equal("Microsoft.Agents.AI.Workflows.DurableWorkflowExecutor", error.MissingTypeName);
        Assert.Contains(error.MissingTypeName, error.Message);
    }

    [Fact]
    public void ValidateRuntimeProfile_AllowsDirectProfile()
    {
        OrchestrationProfileRouter.ValidateRuntimeProfile("Direct");
    }

    [Fact]
    public void ValidateAgentProfile_RejectsDepthBeyondFrameworkLimit() =>
        Assert.Throws<InvalidOperationException>(() => OrchestrationProfileRouter.ValidateAgentProfile(
            new AgentDefinition(new AgentId("test"), "test") { Orchestration = new(MaxDepth: 3) }, true, true, true, true, true));

    [Fact]
    public void ValidateAgentProfile_RejectsUndefinedResultMergeMode() =>
        Assert.Throws<AgentCatalogException>(() => OrchestrationProfileRouter.ValidateAgentProfile(
            new AgentDefinition(new AgentId("test"), "test")
            {
                Orchestration = new(ResultMergeMode: (AgentResultMergeMode)99)
            }, true, true, true, true, true));

    [Fact]
    public void InteractiveIdentity_RequiresActiveUserAndScope()
    {
        OperationalBoundary.AssertInteractive(Context(AgentIdentityMode.ActiveUser, "user:1"));
        Assert.Throws<UnauthorizedAccessException>(() => OperationalBoundary.AssertInteractive(Context(AgentIdentityMode.WorkloadIdentity, "workload:1")));
        Assert.Throws<UnauthorizedAccessException>(() => OperationalBoundary.AssertInteractive(Context(AgentIdentityMode.ActiveUser, " ")));
    }

    [Fact]
    public void OperationalIdentity_RequiresExactConfiguredWorkloadReference()
    {
        OperationalBoundary.AssertOperational(Context(AgentIdentityMode.WorkloadIdentity, "service:worker"), "service:worker");
        Assert.Throws<UnauthorizedAccessException>(() => OperationalBoundary.AssertOperational(Context(AgentIdentityMode.WorkloadIdentity, "service:other"), "service:worker"));
        Assert.Throws<UnauthorizedAccessException>(() => OperationalBoundary.AssertOperational(Context(AgentIdentityMode.ActiveUser, "user:1"), "service:worker"));
    }

    [Theory]
    [InlineData(AgentExecutionMode.Interactive, AgentExecutionMode.Operational)]
    [InlineData(AgentExecutionMode.Operational, AgentExecutionMode.Interactive)]
    public void CrossModeTransition_RequiresExplicitEnqueue(AgentExecutionMode from, AgentExecutionMode to) =>
        Assert.Throws<UnauthorizedAccessException>(() => OperationalBoundary.AssertCrossModeTransition(from, to, false));

    [Fact]
    public void ExplicitCrossModeTransitions_AreAuthorizedAndOperationalToInteractiveRequiresFreshTask()
    {
        Assert.False(OperationalBoundary.AssertCrossModeTransition(AgentExecutionMode.Interactive, AgentExecutionMode.Operational, true).RequiresFreshUserTask);
        Assert.True(OperationalBoundary.AssertCrossModeTransition(AgentExecutionMode.Operational, AgentExecutionMode.Interactive, true).RequiresFreshUserTask);
    }

    [Fact]
    public void SameModeDoesNotRequireExplicitEnqueue() =>
        Assert.False(OperationalBoundary.AssertCrossModeTransition(AgentExecutionMode.Operational, AgentExecutionMode.Operational, false).RequiresFreshUserTask);

    private static AgentExecutionContext Context(AgentIdentityMode identity, string scope) => new()
    {
        TenantScope = "tenant:1", PrincipalOrWorkloadScope = scope, IdentityKind = identity,
        SessionId = new(Ulid.NewUlid()), TaskId = new(Ulid.NewUlid()), AgentExecutionId = new(Ulid.NewUlid()),
        Depth = 0, CatalogRevision = 1, PolicyRevision = 1, AuthRevision = 1,
        Deadline = DateTimeOffset.UtcNow.AddMinutes(1), RemainingBudgets = ImmutableDictionary<string, long>.Empty,
        CancellationToken = CancellationToken.None
    };
}
