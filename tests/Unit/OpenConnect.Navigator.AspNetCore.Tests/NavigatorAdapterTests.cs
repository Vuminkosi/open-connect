using OpenConnect.Navigator;
using Xunit;

namespace OpenConnect.Navigator.AspNetCore.Tests;

public sealed class NavigatorAdapterTests
{
    private static NavigatorTargetDescriptor Descriptor(string id, string route = "home") => new(
        NavigatorTargetRegistry.CurrentSchemaVersion, id, "component", 1, id, "description", route,
        new HashSet<NavigatorActionType> { NavigatorActionType.Observe }, new HashSet<string>(), "public");

    [Fact]
    public void ListByRouteDoesNotExposeAnotherTenantOrPrincipal()
    {
        var registry = new NavigatorTargetRegistry();
        registry.Register(Descriptor("visible"), "tenant-a", "principal-a", 1);
        registry.Register(Descriptor("other-tenant"), "tenant-b", "principal-a", 1);
        registry.Register(Descriptor("other-principal"), "tenant-a", "principal-b", 1);

        var results = registry.ListByRoute("home", new("tenant-a", "principal-a", "home", new HashSet<string>()));

        Assert.Single(results);
        Assert.Equal("visible", results[0].Descriptor.TargetId);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void TryValidateRejectsExpiredOrTamperedGrant(long expiryOffsetSeconds)
    {
        var registry = new NavigatorTargetRegistry();
        var descriptor = Descriptor("target");
        registry.Register(descriptor, "tenant-a", "principal-a", 1);
        var now = DateTimeOffset.UtcNow;
        var grant = new CommandGrant("cmd", "session", 1, NavigatorActionType.Observe, "target", 1,
            CommandGrant.ComputeArgsHash(new { value = "valid" }), "tenant-a", "principal-a", "client", 1,
            now.AddSeconds(expiryOffsetSeconds), "nonce");
        var args = expiryOffsetSeconds < 0 ? "{\"value\":\"valid\"}" : "{\"value\":\"tampered\"}";

        Assert.False(registry.TryValidate(grant, args, "tenant-a", "principal-a", "session", 1, now, out _));
    }
}
