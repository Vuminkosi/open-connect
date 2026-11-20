using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using OpenConnect.LiveUI;
using OpenConnect.LiveUI.Blazor;
using Xunit;

namespace OpenConnect.LiveUI.Blazor.Tests;

public sealed class BrowserLiveUIActionConfirmationTests
{
    [Theory]
    [InlineData(LiveUIActionType.Inspect, LiveUITargetKind.Button, "Open report", false)]
    [InlineData(LiveUIActionType.SelectTab, LiveUITargetKind.Tab, "Active", false)]
    [InlineData(LiveUIActionType.SetValue, LiveUITargetKind.Input, "Search tasks", true)]
    [InlineData(LiveUIActionType.SetValue, LiveUITargetKind.Input, "Task title", false)]
    public async Task Routine_read_and_filter_actions_do_not_open_a_prompt(
        LiveUIActionType actionType, LiveUITargetKind kind, string label, bool filter)
    {
        var browser = new ConfirmationBrowser();
        using var services = CreateServices(browser);
        using var scope = services.CreateScope();
        var context = Context("report");
        var capabilities = new HashSet<LiveUICapability>();
        if (filter) capabilities.Add(LiveUICapability.Filter);
        var target = context.Target! with { Id = "oc.runtime.test", Kind = kind, Label = label, Capabilities = capabilities };
        var action = context.Action with { Type = actionType };
        var confirmation = scope.ServiceProvider.GetRequiredService<ILiveUIActionConfirmation>();
        Assert.True(await confirmation.ConfirmAsync(context with { Target = target, Action = action }));
        Assert.Equal(0, browser.Calls);
    }

    [Theory]
    [InlineData("Delete task")]
    [InlineData("Remove selected")]
    public async Task Destructive_actions_still_require_a_prompt(string label)
    {
        var browser = new ConfirmationBrowser();
        using var services = CreateServices(browser);
        using var scope = services.CreateScope();
        var context = Context("report");
        var target = context.Target! with { Kind = LiveUITargetKind.Button, Label = label };
        var action = context.Action with { Type = LiveUIActionType.Click };
        var confirmation = scope.ServiceProvider.GetRequiredService<ILiveUIActionConfirmation>();
        var pending = confirmation.ConfirmAsync(context with { Target = target, Action = action }).AsTask();
        Assert.Equal(1, browser.Calls);
        browser.Answer.SetResult(false);
        Assert.False(await pending);
    }

    [Fact]
    public async Task Clearing_a_form_value_still_requires_confirmation()
    {
        var browser = new ConfirmationBrowser();
        using var services = CreateServices(browser);
        using var scope = services.CreateScope();
        var context = Context("");
        var target = context.Target! with { Kind = LiveUITargetKind.Input, Label = "Title" };
        var confirmation = scope.ServiceProvider.GetRequiredService<ILiveUIActionConfirmation>();
        var pending = confirmation.ConfirmAsync(context with { Target = target, Action = context.Action with { Type = LiveUIActionType.SetValue } }).AsTask();
        Assert.Equal(1, browser.Calls);
        browser.Answer.SetResult(false);
        Assert.False(await pending);
    }

    [Fact]
    public async Task Automatic_filter_confirmation_does_not_bypass_host_authorization()
    {
        var browser = new ConfirmationBrowser();
        using var services = CreateServices(browser);
        using var scope = services.CreateScope();
        var context = Context("report");
        var target = context.Target! with { Kind = LiveUITargetKind.Input, Label = "Search", Capabilities = new HashSet<LiveUICapability> { LiveUICapability.SetValue, LiveUICapability.Filter } };
        var route = context.Route with { Targets = [target] };
        var manifest = context.Manifest with { Routes = [route], ContentHash = "" };
        manifest = manifest with { ContentHash = LiveUIManifestHasher.ComputeContentHash(manifest) };
        var runtime = new TestRuntime();
        var orchestrator = new LiveUIOrchestrator(new TestManifest(manifest), new TestAuthorizer(false), runtime,
            Options.Create(new LiveUIOptions()), new LiveUIManifestValidator(), [scope.ServiceProvider.GetRequiredService<ILiveUIActionConfirmation>()]);
        var receipt = await orchestrator.ExecuteAsync(context.Action with { Type = LiveUIActionType.SetValue, ManifestHash = manifest.ContentHash });
        Assert.Equal(LiveUIActionOutcome.Denied, receipt.Outcome);
        Assert.Equal(0, runtime.Calls);
        Assert.Equal(0, browser.Calls);
    }

    [Theory]
    [InlineData(false, true, 0, 0)]
    [InlineData(true, false, 1, 0)]
    [InlineData(true, true, 1, 1)]
    public async Task Orchestrator_requires_host_authorization_and_user_confirmation(
        bool hostAllows, bool userAllows, int expectedPrompts, int expectedExecutions)
    {
        var browser = new ConfirmationBrowser();
        using var services = CreateServices(browser);
        using var scope = services.CreateScope();
        var confirmation = scope.ServiceProvider.GetRequiredService<ILiveUIActionConfirmation>();
        var context = Context("Quarterly report");
        var manifest = context.Manifest with { ContentHash = LiveUIManifestHasher.ComputeContentHash(context.Manifest) };
        var action = context.Action with { ManifestHash = manifest.ContentHash };
        var runtime = new TestRuntime();
        var orchestrator = new LiveUIOrchestrator(new TestManifest(manifest), new TestAuthorizer(hostAllows), runtime,
            Options.Create(new LiveUIOptions()), new LiveUIManifestValidator(), [confirmation]);
        var execution = orchestrator.ExecuteAsync(action).AsTask();
        if (hostAllows)
            browser.Answer.SetResult(userAllows);
        var receipt = await execution;
        Assert.Equal(expectedPrompts, browser.Calls);
        Assert.Equal(expectedExecutions, runtime.Calls);
        Assert.Equal(expectedExecutions == 1 ? LiveUIActionOutcome.Completed : LiveUIActionOutcome.Denied, receipt.Outcome);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Confirmation_displays_exact_action_and_returns_browser_decision(bool decision)
    {
        var browser = new ConfirmationBrowser();
        using var services = CreateServices(browser);
        using var scope = services.CreateScope();
        var confirmation = scope.ServiceProvider.GetRequiredService<ILiveUIActionConfirmation>();
        var pending = confirmation.ConfirmAsync(Context("Quarterly report"), CancellationToken.None).AsTask();
        Assert.Equal("confirmAction", browser.Identifier);
        Assert.Contains("Quarterly report", Assert.IsType<string>(browser.Arguments![0]), StringComparison.Ordinal);
        browser.Answer.SetResult(decision);
        Assert.Equal(decision, await pending);
    }

    [Fact]
    public async Task Cancellation_does_not_allow_overlapping_prompts_or_late_approval()
    {
        var browser = new ConfirmationBrowser();
        using var services = CreateServices(browser);
        using var scope = services.CreateScope();
        var confirmation = scope.ServiceProvider.GetRequiredService<ILiveUIActionConfirmation>();
        using var cancellation = new CancellationTokenSource();
        var pending = confirmation.ConfirmAsync(Context("Quarterly report"), cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(await confirmation.ConfirmAsync(Context("Another report")));
        Assert.Equal(1, browser.Calls);
        browser.Answer.SetResult(true);
    }

    [Fact]
    public async Task Unrepresentable_values_are_rejected_without_a_prompt()
    {
        var browser = new ConfirmationBrowser();
        using var services = CreateServices(browser);
        using var scope = services.CreateScope();
        var confirmation = scope.ServiceProvider.GetRequiredService<ILiveUIActionConfirmation>();
        Assert.False(await confirmation.ConfirmAsync(Context("first\nsecond")));
        Assert.False(await confirmation.ConfirmAsync(Context(new string('x', 257))));
        Assert.False(await confirmation.ConfirmAsync(Context("prefix\u202Ehidden")));
        Assert.Equal(0, browser.Calls);
    }

    private static ServiceProvider CreateServices(IJSRuntime jsRuntime)
    {
        var services = new ServiceCollection();
        services.AddSingleton(jsRuntime);
        services.AddOpenConnectLiveUIBlazor();
        return services.BuildServiceProvider();
    }

    private static LiveUIExecutionContext Context(string value)
    {
        var target = new LiveUITarget("title", LiveUITargetKind.Button, "Delete task", new HashSet<LiveUICapability> { LiveUICapability.Activate }, RequiresConfirmation: true);
        var route = new LiveUIRoute("home", "/", "Home", [target]);
        var manifest = new LiveUIManifest(LiveUISchema.CurrentVersion, "build", "hash", [route]);
        var action = new LiveUIAction("action", LiveUISchema.CurrentVersion, "session", "hash", "home", "title", LiveUIActionType.Click, new(Value: value), TimeSpan.FromSeconds(1));
        return new LiveUIExecutionContext(manifest, route, target, action);
    }

    private sealed class TestManifest(LiveUIManifest manifest) : ILiveUIManifestProvider
    {
        public ValueTask<LiveUIManifest?> GetCurrentAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<LiveUIManifest?>(manifest);
    }

    private sealed class TestAuthorizer(bool allowed) : ILiveUIActionAuthorizer
    {
        public ValueTask<LiveUIAuthorizationDecision> AuthorizeAsync(LiveUIExecutionContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new LiveUIAuthorizationDecision(allowed, "policy"));
    }

    private sealed class TestRuntime : ILiveUIRuntime
    {
        public int Calls { get; private set; }
        public ValueTask<LiveUIRuntimeResult> ExecuteAsync(LiveUIExecutionContext context, CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(new LiveUIRuntimeResult(LiveUIActionOutcome.Completed, "verified", true));
        }
    }

    private sealed class ConfirmationBrowser : IJSRuntime, IJSObjectReference
    {
        public string? Identifier { get; private set; }
        public object?[]? Arguments { get; private set; }
        public int Calls { get; private set; }
        public TaskCompletionSource<bool> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "import")
                return (TValue)(object)this;
            if (identifier == "cancelConfirmation")
                return default!;
            Calls++;
            Identifier = identifier;
            Arguments = args;
            return (TValue)(object)await Answer.Task;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
