using OpenConnect.Abstractions;
using OpenConnect.Agents.Templates;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class LeadResolverTests
{
    private static readonly TaskId Task = new(Ulid.Parse("01ARZ3NDEKTSV4RRFFQ69G5FAV"));

    [Fact]
    public void Resolve_ReturnsRegisteredLeadAndCatalogRevision()
    {
        var lead = new AgentDefinition(new AgentId("lead"), "Lead");
        var result = new LeadResolver().Resolve(Task, [lead], new AgentRegistrationExtensions.LeadAgentRegistration(lead.Id));
        Assert.Same(lead, result.Lead);
        Assert.Equal(64, result.CatalogRevision.Length);
        var bound = new LeadResolver().ResolveBound(Task, [lead], new AgentRegistrationExtensions.LeadAgentRegistration(lead.Id));
        Assert.Equal(Task, bound.BoundTaskId);
        Assert.Same(lead, bound.Lead);
    }

    [Fact]
    public void ResolveBound_RejectsDefaultTaskId()
    {
        var lead = new AgentDefinition(new AgentId("lead"), "Lead");
        Assert.Throws<ArgumentException>(() => new LeadResolver().ResolveBound(default, [lead],
            new AgentRegistrationExtensions.LeadAgentRegistration(lead.Id)));
    }

    [Fact]
    public void Resolve_RejectsMissingLead()
    {
        var registration = new AgentRegistrationExtensions.LeadAgentRegistration(new AgentId("missing"));
        Assert.Throws<InvalidOperationException>(() => new LeadResolver().Resolve(Task, [], registration));
    }

    [Fact]
    public void Resolve_RejectsCatalogRevisionChangedAfterRegistration()
    {
        var lead = new AgentDefinition(new AgentId("lead"), "Lead");
        var registration = new AgentRegistrationExtensions.LeadAgentRegistration(lead.Id, new string('0', 64));
        Assert.Throws<InvalidOperationException>(() => new LeadResolver().Resolve(Task, [lead], registration));
    }
}
