using OpenConnect.Agents.Execution;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class MafRuntimeProbeTests
{
    [Fact]
    public void Inventory_reports_pinned_maf_agent_types_and_creation_surface()
    {
        var inventory = MafRuntimeProbe.GetAgentApiInventory();

        Assert.NotEmpty(inventory);
        Assert.Contains(inventory, item => item.Contains("Microsoft.Agents.AI.AIAgent", StringComparison.Ordinal));

        // Keep the exact package surface visible in test output for deciding whether the local scaffold can be backed.
        Console.WriteLine(string.Join(Environment.NewLine + "---" + Environment.NewLine, inventory));
    }

    [Fact]
    public void Workflow_capability_matrix_reports_actual_type_presence_or_exact_missing_type()
    {
        var matrix = MafRuntimeProbe.GetWorkflowCapabilityMatrix();

        Assert.Contains(matrix, capability => capability.Capability == "Workflow");
        Assert.All(matrix, capability =>
        {
            if (capability.Status == MafCapabilityStatus.Unavailable)
            {
                Assert.Equal($"Missing type: {capability.RequiredTypeName}", capability.UnavailableReason);
                Assert.Null(capability.AssemblyName);
            }
            else
            {
                Assert.Null(capability.UnavailableReason);
                Assert.NotNull(capability.AssemblyName);
            }
        });

        Console.WriteLine(string.Join(Environment.NewLine, matrix.Select(item =>
            $"{item.Capability}: {item.Status}; type={item.RequiredTypeName}; assembly={item.AssemblyName ?? "-"}; reason={item.UnavailableReason ?? "-"}")));
    }
}
