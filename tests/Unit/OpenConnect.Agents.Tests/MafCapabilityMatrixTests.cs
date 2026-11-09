using Microsoft.Agents.AI.Workflows.Declarative;
using Microsoft.Agents.AI.Workflows.Declarative.Mcp;
using OpenConnect.Agents.Execution;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class MafCapabilityMatrixTests
{
    [Fact]
    public void Harness_profile_request_passes_through_when_harness_type_present()
    {
        // Microsoft.Agents.AI.Harness 1.27.0 is referenced, so the Harness
        // gate resolves Available and ValidateExecutionProfile must not throw.
        var exception = Record.Exception(
            () => OrchestrationProfileRouter.ValidateExecutionProfile("Harness"));

        Assert.Null(exception);
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
    [InlineData("A2A", "A2A.A2AClient")]
    public void Remote_agent_profile_requests_throw_unavailable_with_missing_type(string profile, string expectedMissingType)
    {
        var exception = Assert.Throws<MafCapabilityUnavailableException>(
            () => OrchestrationProfileRouter.ValidateExecutionProfile(profile));

        Assert.Equal(profile, exception.Profile);
        Assert.Equal(expectedMissingType, exception.MissingTypeName);
    }

    [Theory]
    [InlineData("Durable execution", "Microsoft.Agents.AI.Workflows.DurableWorkflowExecutor")]
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
    public void Mcp_profile_request_passes_through_when_declarative_mcp_type_present()
    {
        // Microsoft.Agents.AI.Workflows.Declarative.Mcp 1.27.0 is referenced, so the MCP
        // gate resolves Available and ValidateExecutionProfile must not throw.
        var exception = Record.Exception(
            () => OrchestrationProfileRouter.ValidateExecutionProfile("MCP"));

        Assert.Null(exception);
    }

    [Fact]
    public void Mcp_capability_reports_available_with_declarative_mcp_assembly()
    {
        // The Declarative.Mcp package is referenced, so the probe must resolve
        // the MCP integration entry as Available with the real adapter type and
        // its owning assembly.
        var matrix = MafRuntimeProbe.GetWorkflowCapabilityMatrix();

        var mcp = Assert.Single(matrix, item => item.Capability == "MCP integration");

        Assert.Equal("Microsoft.Agents.AI.Workflows.Declarative.Mcp.DefaultMcpToolHandler", mcp.RequiredTypeName);
        Assert.Equal(MafCapabilityStatus.Available, mcp.Status);
        Assert.Null(mcp.UnavailableReason);
        Assert.Equal("Microsoft.Agents.AI.Workflows.Declarative.Mcp", mcp.AssemblyName);
    }

    [Fact]
    public void Declarative_mcp_adapter_implements_tool_handler_with_async_dispose()
    {
        // Minimal local evidence that the native MAF adapter is resolvable: no
        // server, no network, no instantiation. The adapter exposes DisposeAsync
        // (IAsyncDisposable), not the synchronous IDisposable pattern.
        Assert.True(typeof(IMcpToolHandler).IsAssignableFrom(typeof(DefaultMcpToolHandler)));
        Assert.True(typeof(IAsyncDisposable).IsAssignableFrom(typeof(DefaultMcpToolHandler)));
        Assert.NotNull(typeof(DefaultMcpToolHandler).GetMethod("DisposeAsync", Type.EmptyTypes));
    }

    [Fact]
    public void Harness_capability_reports_available_with_harness_assembly()
    {
        // The Microsoft.Agents.AI.Harness package is referenced, so the
        // probe must resolve the Harness entry as Available with the real
        // fully-qualified agent type and its owning assembly.
        var matrix = MafRuntimeProbe.GetWorkflowCapabilityMatrix();

        var harness = Assert.Single(matrix, item => item.Capability == "Harness");

        Assert.Equal("Microsoft.Agents.AI.HarnessAgent", harness.RequiredTypeName);
        Assert.Equal(MafCapabilityStatus.Available, harness.Status);
        Assert.Null(harness.UnavailableReason);
        Assert.Equal("Microsoft.Agents.AI.Harness", harness.AssemblyName);
    }

    [Fact]
    public void Workflow_capability_reports_available_with_assembly()
    {
        // The Microsoft.Agents.AI.Workflows package is referenced, so the
        // probe must resolve the Workflow entry as Available (fail closed
        // applies only to the unsupported Durable/A2A entries).
        var matrix = MafRuntimeProbe.GetWorkflowCapabilityMatrix();

        var workflow = Assert.Single(matrix, item => item.Capability == "Workflow");

        Assert.Equal("Microsoft.Agents.AI.Workflows.WorkflowBuilder", workflow.RequiredTypeName);
        Assert.Equal(MafCapabilityStatus.Available, workflow.Status);
        Assert.Null(workflow.UnavailableReason);
        Assert.NotNull(workflow.AssemblyName);
    }

    [Fact]
    public async Task Unsupported_durable_profile_throws_before_any_execution_occurs()
    {
        var runInvoked = false;
        Task RunAsync()
        {
            runInvoked = true;
            return Task.CompletedTask;
        }

        var exception = await Assert.ThrowsAsync<MafCapabilityUnavailableException>(
            () => ExecuteProfileAsync("DurableWorkflow", RunAsync));

        Assert.Equal("DurableWorkflow", exception.Profile);
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
