using OpenConnect.Agents.Execution;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class MafCapabilityMatrixTests
{
    [Fact]
    public void Harness_profile_request_throws_unavailable_with_missing_type()
    {
        var exception = Assert.Throws<MafCapabilityUnavailableException>(
            () => OrchestrationProfileRouter.ValidateExecutionProfile("Harness"));

        Assert.Equal("Harness", exception.Profile);
        Assert.Equal("Microsoft.Agents.AI.HarnessAgent", exception.MissingTypeName);
    }

    [Fact]
    public void DurableWorkflow_profile_request_throws_unavailable_with_missing_type()
    {
        var exception = Assert.Throws<MafCapabilityUnavailableException>(
            () => OrchestrationProfileRouter.ValidateExecutionProfile("DurableWorkflow"));

        Assert.Equal("DurableWorkflow", exception.Profile);
        Assert.Equal("Microsoft.Agents.AI.Workflows.DurableWorkflowExecutor", exception.MissingTypeName);
    }

    [Theory]
    [InlineData("MCP", "ModelContextProtocol.Client.McpClient")]
    [InlineData("A2A", "A2A.A2AClient")]
    public void Remote_agent_profile_requests_throw_unavailable_with_missing_type(string profile, string expectedMissingType)
    {
        var exception = Assert.Throws<MafCapabilityUnavailableException>(
            () => OrchestrationProfileRouter.ValidateExecutionProfile(profile));

        Assert.Equal(profile, exception.Profile);
        Assert.Equal(expectedMissingType, exception.MissingTypeName);
    }

    [Theory]
    [InlineData("Harness", "Microsoft.Agents.AI.HarnessAgent")]
    [InlineData("Durable execution", "Microsoft.Agents.AI.Workflows.DurableWorkflowExecutor")]
    [InlineData("MCP integration", "ModelContextProtocol.Client.McpClient")]
    [InlineData("A2A integration", "A2A.A2AClient")]
    public void Unsupported_capabilities_report_unavailable_with_exact_missing_type(string capability, string expectedTypeName)
    {
        var matrix = MafRuntimeProbe.GetWorkflowCapabilityMatrix();

        var probe = Assert.Single(matrix, item => item.Capability == capability);

        Assert.Equal(MafCapabilityStatus.Unavailable, probe.Status);
        Assert.Equal(expectedTypeName, probe.RequiredTypeName);
        Assert.Equal($"Missing type: {expectedTypeName}", probe.UnavailableReason);
        Assert.Null(probe.AssemblyName);
    }

    [Fact]
    public void Workflow_capability_reports_available_with_assembly()
    {
        // The Microsoft.Agents.AI.Workflows package is referenced, so the
        // probe must resolve the Workflow entry as Available (fail closed
        // applies only to the unsupported Harness/Durable/MCP/A2A entries).
        var matrix = MafRuntimeProbe.GetWorkflowCapabilityMatrix();

        var workflow = Assert.Single(matrix, item => item.Capability == "Workflow");

        Assert.Equal("Microsoft.Agents.AI.Workflows.WorkflowBuilder", workflow.RequiredTypeName);
        Assert.Equal(MafCapabilityStatus.Available, workflow.Status);
        Assert.Null(workflow.UnavailableReason);
        Assert.NotNull(workflow.AssemblyName);
    }

    [Fact]
    public async Task Unsupported_profile_throws_before_any_execution_occurs()
    {
        var runInvoked = false;
        Task RunAsync()
        {
            runInvoked = true;
            return Task.CompletedTask;
        }

        var exception = await Assert.ThrowsAsync<MafCapabilityUnavailableException>(
            () => ExecuteProfileAsync("Harness", RunAsync));

        Assert.Equal("Harness", exception.Profile);
        Assert.False(runInvoked);
    }

    [Fact]
    public void Unknown_execution_profile_throws_without_execution()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => OrchestrationProfileRouter.ValidateExecutionProfile("Teleport"));
        Assert.IsType<InvalidOperationException>(exception);
    }

    private static async Task ExecuteProfileAsync(string profile, Func<Task> runAsync)
    {
        // Gate first: throws MafCapabilityUnavailableException for unsupported profiles,
        // so runAsync below is unreachable on those paths.
        OrchestrationProfileRouter.ValidateExecutionProfile(profile);
        await runAsync();
    }
}
