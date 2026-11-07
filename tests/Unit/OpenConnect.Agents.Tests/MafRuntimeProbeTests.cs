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
}
