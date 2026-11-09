using OpenConnect.Abstractions;
using OpenConnect.Agents.Catalog;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class OperationalDefinitionTests
{
    [Fact]
    public void OperationalDefinition_WithWorkloadIdentityAndOnDemandActivation_IsValid()
    {
        AgentCatalog.Validate([Operational()]);
    }

    [Fact]
    public void OperationalDefinition_WithActiveUserIdentity_IsRejected()
    {
        Assert.Throws<AgentCatalogException>(() => AgentCatalog.Validate([Operational() with { IdentityMode = AgentIdentityMode.ActiveUser }]));
    }

    [Fact]
    public void OperationalDefinition_WithoutWorkloadIdentity_IsRejected()
    {
        Assert.Throws<AgentCatalogException>(() => AgentCatalog.Validate([Operational() with { Operational = new() }]));
    }

    [Theory]
    [InlineData("not a cron", "UTC")]
    [InlineData("0 0 * * *", "Not/A-Time-Zone")]
    public void ScheduledOperationalDefinition_WithInvalidCronOrTimeZone_IsRejected(string cron, string timeZone)
    {
        var definition = Operational() with
        {
            Operational = new(AgentActivationMode.Scheduled, "agent-workload", cron, timeZone)
        };
        Assert.Throws<AgentCatalogException>(() => AgentCatalog.Validate([definition]));
    }

    [Fact]
    public void ScheduledOperationalDefinition_WithMalformedTimeZone_WrapsCause()
    {
        var definition = Operational() with
        {
            Operational = new(AgentActivationMode.Scheduled, "agent-workload", "0 0 * * *", "Not/A-Time-Zone")
        };

        var exception = Assert.Throws<AgentCatalogException>(() => AgentCatalog.Validate([definition]));

        // An unknown zone name produces TimeZoneNotFoundException on Windows and Linux;
        // the validation catch also handles InvalidTimeZoneException/ArgumentException
        // for corrupt-data or malformed cases.
        Assert.IsType<TimeZoneNotFoundException>(exception.InnerException);
    }

    [Theory]
    [InlineData(AgentActivationMode.Scheduled)]
    [InlineData(AgentActivationMode.Continuous)]
    public void InteractiveDefinition_WithNonOnDemandActivation_IsRejected(AgentActivationMode activation)
    {
        var definition = new AgentDefinition(new AgentId("interactive"), "interactive")
        {
            Operational = new(Activation: activation)
        };
        Assert.Throws<AgentCatalogException>(() => AgentCatalog.Validate([definition]));
    }

    [Fact]
    public void ExistingInteractiveDefinition_DefaultsRemainValid()
    {
        var definition = new AgentDefinition(new AgentId("interactive"), "interactive");
        Assert.Null(definition.Operational);
        Assert.Equal(AgentExecutionMode.Interactive, definition.ExecutionMode);
        Assert.Equal(AgentIdentityMode.ActiveUser, definition.IdentityMode);
        AgentCatalog.Validate([definition]);
    }

    private static AgentDefinition Operational() => new(new AgentId("operational"), "operational")
    {
        ExecutionMode = AgentExecutionMode.Operational,
        IdentityMode = AgentIdentityMode.WorkloadIdentity,
        Operational = new(WorkloadIdentityRef: "agent-workload")
    };
}
