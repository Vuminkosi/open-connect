using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class MafAgentFactoryTests
{
    [Fact]
    public void Delegation_records_parent_child_trace()
    {
        var registry = new TestRegistry();
        var leadId = new AgentId("lead");
        var childId = new AgentId("child");
        registry.Register(new AgentDefinition(leadId, "Lead"));
        registry.Register(new AgentDefinition(childId, "Child"));
        var factory = new MafAgentFactory(registry);

        var lead = factory.CreateLeadAgent(leadId);
        var child = factory.Delegate(lead, childId);
        var traces = factory.GetTrace();

        Assert.Equal(2, traces.Count);
        Assert.Null(traces[0].ParentId);
        Assert.Equal(lead.Id, traces[1].ParentId);
        Assert.Equal("delegation", traces[1].Kind);
        Assert.Equal("child", traces[1].AgentId);
    }

    private sealed class TestRegistry : IAgentDefinitionRegistry
    {
        private readonly Dictionary<AgentId, AgentDefinition> _agents = [];
        public AgentDefinition? Get(AgentId id) => _agents.GetValueOrDefault(id);
        public IReadOnlyList<AgentDefinition> GetAll() => _agents.Values.ToArray();
        public void Register(AgentDefinition definition) => _agents[definition.Id] = definition;
    }
}
