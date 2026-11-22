using System.Reflection;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

public class CloudCapabilityDependencyTests
{
    [Fact]
    public void ApplicationRequiresItsOwnRecursiveDependencySelections()
    {
        var root = Capability("root", "[\"middle\"]");
        var middle = Capability("middle", "[\"leaf\"]");
        var leaf = Capability("leaf", "[]");
        CloudCapabilityDefinition[] catalog = [root, middle, leaf];
        var entitlements = catalog.ToDictionary(x => x.Id, x => new CloudTenantEntitlement { CapabilityId = x.Id, PlanGranted = true });
        var tenantSelections = catalog.ToDictionary(x => x.Id, x => new CloudTenantCapabilitySelection { CapabilityId = x.Id, Enabled = true });
        var appSelections = new Dictionary<string, CloudApplicationCapabilitySelection>
        {
            ["middle"] = new() { CapabilityId = "middle", Enabled = true },
        };
        var method = typeof(CloudCapabilityService).Assembly.GetType("OpenConnect.Docs.Cloud.CloudCapabilityPolicy")!.GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(x => x.Name == "DependenciesEnabled" && x.GetParameters().Length == 6);

        bool Evaluate() => (bool)method.Invoke(null, [root, catalog, entitlements, tenantSelections, DateTimeOffset.UtcNow, appSelections])!;

        Assert.False(Evaluate());
        appSelections["leaf"] = new() { CapabilityId = "leaf", Enabled = true };
        Assert.True(Evaluate());
        appSelections["leaf"].Enabled = false;
        Assert.False(Evaluate());
    }

    private static CloudCapabilityDefinition Capability(string id, string dependencies) => new()
    {
        Id = id,
        Name = id,
        Description = id,
        ContractVersion = "1",
        BillingUnit = "request",
        PlatformAvailability = "available",
        Dependencies = dependencies,
        Modalities = "[]",
        AllowedRegions = "[]",
    };
}
