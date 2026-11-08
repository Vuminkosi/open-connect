using OpenConnect.Navigator;
using Xunit;

namespace OpenConnect.Navigator.Tests;

public sealed class GrantTests
{
    [Fact]
    public void Registry_lists_only_route_tenant_principal_and_capability_visible_targets_and_accepts_mutations()
    {
        var registry = new NavigatorTargetRegistry();
        var target = new NavigatorTargetDescriptor(NavigatorTargetRegistry.CurrentSchemaVersion, "save", "form", 1,
            "Save", "Save form", "/edit", new HashSet<NavigatorActionType> { NavigatorActionType.Click },
            new HashSet<string> { "edit" }, "normal");
        registry.Register(target, "tenant", "principal", 4);
        Assert.Single(registry.ListByRoute("/edit", new("tenant", "principal", "/edit", new HashSet<string> { "edit" })));
        Assert.Empty(registry.ListByRoute("/edit", new("other", "principal", "/edit", new HashSet<string> { "edit" })));
        Assert.Empty(registry.ListByRoute("/edit", new("tenant", "principal", "/edit", new HashSet<string>())));
        Assert.Throws<ArgumentException>(() => registry.Register(target with { AllowedActions = new HashSet<NavigatorActionType> { (NavigatorActionType)999 } }, "tenant", "principal", 5));
    }

    [Fact]
    public void Registry_validates_grant_digest_expiry_and_bindings()
    {
        var now = DateTimeOffset.UtcNow;
        var json = "{\"z\":2,\"a\":1}";
        using var args = System.Text.Json.JsonDocument.Parse(json);
        var grant = new CommandGrant("cmd", "session", 3, NavigatorActionType.Click, "save", 1,
            CommandGrant.ComputeArgsHash(args.RootElement), "tenant", "principal", "client", 2, now.AddMinutes(1), "nonce");
        var registry = new NavigatorTargetRegistry();
        registry.Register(new NavigatorTargetDescriptor(NavigatorTargetRegistry.CurrentSchemaVersion, "save", "form", 1,
            "Save", "Save form", "/edit", new HashSet<NavigatorActionType> { NavigatorActionType.Click },
            new HashSet<string>(), "normal"), "tenant", "principal", 1);
        Assert.True(registry.TryValidate(grant, json, "tenant", "principal", "session", 2, now, out var reason));
        Assert.Equal(string.Empty, reason);
        Assert.False(registry.TryValidate(grant, "{\"z\":3,\"a\":1}", "tenant", "principal", "session", 2, now, out _));
        Assert.False(registry.TryValidate(grant, json, "other", "principal", "session", 2, now, out _));
        Assert.False(registry.TryValidate(grant, json, "tenant", "principal", "other-session", 2, now, out _));
        Assert.False(registry.TryValidate(grant, json, "tenant", "principal", "session", 3, now, out _));
        Assert.False(registry.TryValidate(grant, json, "tenant", "principal", "session", 2, grant.ExpiresAt, out _));
    }

    [Fact]
    public void Grant_rejects_digest_mismatch_and_expiry()
    {
        var now = DateTimeOffset.UtcNow;
        var args = new { value = "safe", count = 2 };
        var grant = new CommandGrant("cmd", "session", 3, NavigatorActionType.Click, "target", 1,
            CommandGrant.ComputeArgsHash(args), "tenant", "principal", "client", 2, now.AddMinutes(1), "nonce");
        Assert.True(grant.IsValidFor("session", "tenant", "principal", "client", "cmd", 3,
            NavigatorActionType.Click, "target", 1, 2, new { count = 2, value = "safe" }, now));
        Assert.False(grant.IsValidFor("session", "tenant", "principal", "client", "cmd", 3,
            NavigatorActionType.Click, "target", 1, 2, new { value = "different", count = 2 }, now));
        Assert.False(grant.IsValidFor("session", "tenant", "principal", "client", "cmd", 3,
            NavigatorActionType.Click, "target", 1, 2, args, now.AddMinutes(1)));
    }

    [Fact]
    public void Approval_is_exact_digest_expiring_and_one_shot()
    {
        var now = DateTimeOffset.UtcNow;
        var digest = CommandGrant.ComputeArgsHash(new { value = 1 });
        var approval = new NavigatorApproval(digest, now.AddMinutes(1));
        Assert.False(approval.TryConsume(CommandGrant.ComputeArgsHash(new { value = 2 }), now));
        Assert.True(approval.TryConsume(digest, now));
        Assert.False(approval.TryConsume(digest, now));
        Assert.False(new NavigatorApproval(digest, now).TryConsume(digest, now));
    }

    [Theory]
    [InlineData(NavigatorCommandOutcome.Completed)]
    [InlineData(NavigatorCommandOutcome.Denied)]
    [InlineData(NavigatorCommandOutcome.Uncertain)]
    [InlineData(NavigatorCommandOutcome.Failed)]
    public void Receipt_preserves_outcome(NavigatorCommandOutcome outcome)
    {
        var receipt = new NavigatorReceipt("intent", NavigatorActionType.Click, outcome, null);
        Assert.Equal(outcome, receipt.Outcome);
    }
}
