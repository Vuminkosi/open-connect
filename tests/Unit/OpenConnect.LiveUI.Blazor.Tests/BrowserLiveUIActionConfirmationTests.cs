using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Microsoft.Extensions.Options;
using OpenConnect.LiveUI;
using OpenConnect.LiveUI.Blazor;
using Xunit;

namespace OpenConnect.LiveUI.Blazor.Tests;

public sealed class BrowserLiveUIActionConfirmationTests
{
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
        if (hostAllows) browser.Answer.SetResult(userAllows);
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
        var target = new LiveUITarget("title", LiveUITargetKind.Input, "Task title", new HashSet<LiveUICapability> { LiveUICapability.SetValue }, RequiresConfirmation: true);
        var route = new LiveUIRoute("home", "/", "Home", [target]);
        var manifest = new LiveUIManifest(LiveUISchema.CurrentVersion, "build", "hash", [route]);
        var action = new LiveUIAction("action", LiveUISchema.CurrentVersion, "session", "hash", "home", "title", LiveUIActionType.SetValue, new(Value: value), TimeSpan.FromSeconds(1));
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
            if (identifier == "import") return (TValue)(object)this;
            if (identifier == "cancelConfirmation") return default!;
            Calls++;
            Identifier = identifier;
            Arguments = args;
            return (TValue)(object)await Answer.Task;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
