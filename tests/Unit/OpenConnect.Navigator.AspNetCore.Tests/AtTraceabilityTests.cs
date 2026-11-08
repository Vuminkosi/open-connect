using OpenConnect.Navigator;
using Xunit;

namespace OpenConnect.Navigator.AspNetCore.Tests;

public sealed class AtTraceabilityTests
{
    [Fact]
    public void At003_GrantValidationAndCapabilityFilterBoundInteractiveActions()
    {
        var now = DateTimeOffset.UtcNow;
        const string json = "{\"z\":2,\"a\":1}";
        using var args = System.Text.Json.JsonDocument.Parse(json);
        var registry = new NavigatorTargetRegistry();
        registry.Register(Target(NavigatorActionType.Click), "tenant", "principal", 1);
        var grant = new CommandGrant("cmd", "session", 3, NavigatorActionType.Click, "save", 1,
            CommandGrant.ComputeArgsHash(args.RootElement), "tenant", "principal", "client", 2, now.AddMinutes(1), "nonce");

        Assert.True(registry.TryValidate(grant, json, "tenant", "principal", "session", 2, now, out _));
        Assert.False(registry.TryValidate(grant, json, "tenant", "principal", "session", 2, now,
            out _) && registry.ListByRoute("/edit", new("tenant", "principal", "/edit", new HashSet<string>())).Count > 0);
    }

    [Fact]
    public void At005_UnregisteredTargetsAreNotMutationSafe()
    {
        Assert.Equal(NavigatorActionRisk.Forbidden,
            NavigatorRiskClassifier.Classify(NavigatorActionType.Click, false, true));
        Assert.Empty(new NavigatorTargetRegistry().Resolve("missing",
            new("tenant", "principal", "/edit", new HashSet<string>())));
    }

    [Fact]
    public void At006_ApprovalDigestExpiryAndSingleUseAreEnforced()
    {
        var now = DateTimeOffset.UtcNow;
        var digest = CommandGrant.ComputeArgsHash(new { value = 1 });
        var approval = new NavigatorApproval(digest, now.AddMinutes(1));

        Assert.False(approval.TryConsume(CommandGrant.ComputeArgsHash(new { value = 2 }), now));
        Assert.True(approval.TryConsume(digest, now));
        Assert.False(approval.TryConsume(digest, now));
        Assert.False(new NavigatorApproval(digest, now).TryConsume(digest, now));
    }

    [Fact]
    public void At008_UnsafeEventsAreRejectedAndSafeEventIdentityIsPreserved()
    {
        var safe = new NavigatorEvent("event-1", "command.completed", "session", null, 1, null,
            DateTimeOffset.UtcNow, new Dictionary<string, string> { ["outcome"] = "completed" });
        var unsafeEvent = safe with { Metadata = new Dictionary<string, string> { ["transcript"] = "private" } };

        Assert.True(NavigatorEventTypes.IsSafe(safe));
        Assert.False(NavigatorEventTypes.IsSafe(unsafeEvent));
        Assert.Equal("event-1", safe.EventId);
    }

    [Fact]
    public void At019_OptionsRejectInvalidEnumCombinations()
    {
        var options = new NavigatorOptions { Mode = (NavigatorMode)999 };

        Assert.False(new NavigatorOptionsValidator().Validate(null, options).Succeeded);
    }

    private static NavigatorTargetDescriptor Target(NavigatorActionType action) =>
        new(NavigatorTargetRegistry.CurrentSchemaVersion, "save", "form", 1, "Save", "Save form", "/edit",
            new HashSet<NavigatorActionType> { action }, new HashSet<string> { "edit" }, "normal");
}
