using OpenConnect.Navigator;
using Xunit;

namespace OpenConnect.Navigator.Tests;

public sealed class NavigatorTests
{
    [Fact]
    public void Options_defaults_are_text_only_and_valid()
    {
        var options = new NavigatorOptions();
        Assert.False(options.Speech.Enabled);
        Assert.Equal(NavigatorBrowserProvider.Disabled, options.Browser.Provider);
        Assert.True(new NavigatorOptionsValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void Options_reject_invalid_enum_combinations()
    {
        var options = new NavigatorOptions();
        options.Speech.PreferredProvider = NavigatorSpeechProvider.MmsTts;
        Assert.False(new NavigatorOptionsValidator().Validate(null, options).Succeeded);
        options = new NavigatorOptions();
        options.Mode = (NavigatorMode)999;
        Assert.False(new NavigatorOptionsValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void Registry_scopes_targets_by_tenant_principal_route_and_capability()
    {
        var registry = new NavigatorTargetRegistry();
        var target = new NavigatorTargetDescriptor(NavigatorTargetRegistry.CurrentSchemaVersion, "help", "docs", 1, "Help", "Help link", "/docs",
            new HashSet<NavigatorActionType> { NavigatorActionType.Highlight }, new HashSet<string> { "guide" }, "public");
        registry.Register(target, "t1", "p1", 1);
        Assert.Single(registry.Resolve("help", new("t1", "p1", "/docs", new HashSet<string> { "guide" })));
        Assert.Empty(registry.Resolve("help", new("t2", "p1", "/docs", new HashSet<string> { "guide" })));
        Assert.Empty(registry.Resolve("help", new("t1", "p1", "/docs", new HashSet<string>())));
    }

    [Fact]
    public void Tour_validator_bounds_and_disallows_unverified_mutation()
    {
        var validator = new NavigatorTourValidator();
        var tooLong = new NavigatorTourPlan("tour", 1, Enumerable.Repeat(new NavigatorTourStep(NavigatorTourStepType.Explain), 26).ToArray(), "/");
        Assert.Throws<ArgumentException>(() => validator.Validate(tooLong));
        var mutation = new NavigatorTourPlan("tour", 1, [new(NavigatorTourStepType.Execute, "x", Action: NavigatorActionType.Click)], "/");
        Assert.Throws<ArgumentException>(() => validator.Validate(mutation));
    }

    [Fact]
    public void Risk_classifier_forbids_unregistered_or_unapproved_mutation()
    {
        Assert.Equal(NavigatorActionRisk.Forbidden, NavigatorRiskClassifier.Classify(NavigatorActionType.Click, false, true));
        Assert.Equal(NavigatorActionRisk.Forbidden, NavigatorRiskClassifier.Classify(NavigatorActionType.Fill, true, false));
        Assert.Equal(NavigatorActionRisk.Mutation, NavigatorRiskClassifier.Classify(NavigatorActionType.Click, true, true));
    }

    [Fact]
    public void Session_enforces_valid_transitions()
    {
        var session = new NavigatorSession("tenant", "principal");
        session.TransitionTo(NavigatorSessionState.Ready);
        session.TransitionTo(NavigatorSessionState.Running);
        session.TransitionTo(NavigatorSessionState.Completing);
        session.TransitionTo(NavigatorSessionState.Completed);
        Assert.Throws<InvalidOperationException>(() => session.TransitionTo(NavigatorSessionState.Running));
    }
}
