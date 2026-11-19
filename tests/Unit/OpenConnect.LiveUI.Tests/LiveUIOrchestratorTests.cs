using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Options;
using OpenConnect.LiveUI;
using Xunit;

namespace OpenConnect.LiveUI.Tests;

public sealed class LiveUIOrchestratorTests
{
    [Fact]
    public void Authorization_diagnostics_are_optional_and_legacy_readers_ignore_them()
    {
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var legacy = new LiveUIActionReceipt("action", "session", LiveUIActionOutcome.Uncertain, "test", false, DateTimeOffset.UtcNow);
        var oldJson = System.Text.Json.JsonSerializer.Serialize(legacy, options);
        Assert.DoesNotContain("authorizationOutcome", oldJson, StringComparison.Ordinal);
        Assert.Null(System.Text.Json.JsonSerializer.Deserialize<LiveUIActionReceipt>(oldJson, options)!.AuthorizationOutcome);
        var json = System.Text.Json.JsonSerializer.Serialize(legacy with { AuthorizationOutcome = LiveUIAuthorizationOutcome.Allowed }, options);
        Assert.Equal(LiveUIAuthorizationOutcome.Allowed,
            System.Text.Json.JsonSerializer.Deserialize<LiveUIActionReceipt>(json, options)!.AuthorizationOutcome);
        Assert.Equal("action", System.Text.Json.JsonSerializer.Deserialize<LegacyReceipt>(json, options)!.ActionId);
    }

    private sealed record LegacyReceipt(string ActionId);

    [Fact]
    public void ManifestJson_RoundTripsReadOnlyCapabilitySet()
    {
        var manifest = new LiveUIManifest(
            LiveUISchema.CurrentVersion,
            "build-1",
            "hash",
            [new LiveUIRoute("route.home", "/", "Home",
                [new LiveUITarget("target.search", LiveUITargetKind.Input, "Search",
                    new HashSet<LiveUICapability> { LiveUICapability.Filter, LiveUICapability.SetValue })])]);
        var options = LiveUIManifestJson.CreateSerializerOptions();

        var json = System.Text.Json.JsonSerializer.Serialize(manifest, options);
        var result = System.Text.Json.JsonSerializer.Deserialize<LiveUIManifest>(json, options);

        Assert.NotNull(result);
        Assert.Contains(LiveUICapability.Filter, Assert.Single(Assert.Single(result.Routes).Targets).Capabilities);
    }

    private static readonly LiveUIManifest Manifest = CreateManifest();

    [Theory]
    [InlineData(false, true, true, 0, 0)]
    [InlineData(true, false, true, 1, 0)]
    [InlineData(true, true, false, 1, 0)]
    [InlineData(true, true, true, 1, 1)]
    public async Task Confirmation_never_bypasses_host_authorization(bool firstAllow, bool confirmed, bool secondAllow,
        int expectedConfirmations, int expectedExecutions)
    {
        var route = Manifest.Routes[0];
        var manifest = Manifest with { Routes = [route with { Targets = [route.Targets[0] with { RequiresConfirmation = true }] }] };
        manifest = manifest with { ContentHash = LiveUIManifestHasher.ComputeContentHash(manifest) };
        var authorizer = new SequencedAuthorizer(firstAllow, secondAllow);
        var confirmation = new TestConfirmation(confirmed);
        var runtime = new RecordingRuntime();
        var orchestrator = new LiveUIOrchestrator(new RecordingManifestProvider(manifest), authorizer, runtime,
            Options.Create(new LiveUIOptions()), new LiveUIManifestValidator(), [confirmation]);

        var action = Action(hash: manifest.ContentHash);
        var receipt = await orchestrator.ExecuteAsync(action);

        Assert.Equal(expectedConfirmations, confirmation.Calls);
        Assert.Equal(expectedExecutions, runtime.Calls);
        Assert.Equal(expectedExecutions == 1 ? LiveUIActionOutcome.Completed : LiveUIActionOutcome.Denied, receipt.Outcome);
        Assert.Equal(!firstAllow || confirmed && !secondAllow ? LiveUIAuthorizationOutcome.Denied : LiveUIAuthorizationOutcome.Allowed,
            receipt.AuthorizationOutcome);
        if (expectedConfirmations == 1) Assert.Equal(action.ActionId, confirmation.ActionId);
    }

    [Fact]
    public async Task Confirmation_does_not_dispatch_after_manifest_changes()
    {
        var route = Manifest.Routes[0];
        var manifest = Manifest with { Routes = [route with { Targets = [route.Targets[0] with { RequiresConfirmation = true }] }] };
        manifest = manifest with { ContentHash = LiveUIManifestHasher.ComputeContentHash(manifest) };
        var provider = new ChangingManifestProvider(manifest);
        var runtime = new RecordingRuntime();
        var orchestrator = new LiveUIOrchestrator(provider, new RecordingAuthorizer(true), runtime,
            Options.Create(new LiveUIOptions()), new LiveUIManifestValidator(), [new TestConfirmation(true)]);

        var receipt = await orchestrator.ExecuteAsync(Action(hash: manifest.ContentHash));

        Assert.Equal(LiveUIActionOutcome.Conflict, receipt.Outcome);
        Assert.Equal("manifest_stale", receipt.Code);
        Assert.Equal(0, runtime.Calls);
    }

    [Fact]
    public async Task Confirmation_timeout_denies_and_late_confirmation_does_not_dispatch()
    {
        var route = Manifest.Routes[0];
        var manifest = Manifest with { Routes = [route with { Targets = [route.Targets[0] with { RequiresConfirmation = true }] }] };
        manifest = manifest with { ContentHash = LiveUIManifestHasher.ComputeContentHash(manifest) };
        var confirmation = new DelayedConfirmation();
        var runtime = new RecordingRuntime();
        var orchestrator = new LiveUIOrchestrator(new RecordingManifestProvider(manifest), new RecordingAuthorizer(true), runtime,
            Options.Create(new LiveUIOptions()), new LiveUIManifestValidator(), [confirmation]);

        var receipt = await orchestrator.ExecuteAsync(Action(hash: manifest.ContentHash) with { Timeout = TimeSpan.FromMilliseconds(50) });
        Assert.Equal(LiveUIActionOutcome.Denied, receipt.Outcome);
        Assert.Equal("approval_timeout", receipt.Code);
        Assert.Equal(LiveUIAuthorizationOutcome.Allowed, receipt.AuthorizationOutcome);
        confirmation.Release.SetResult(true);
        Assert.Equal(0, runtime.Calls);
    }

    [Fact]
    public async Task Multiple_confirmation_providers_fail_closed()
    {
        var route = Manifest.Routes[0];
        var manifest = Manifest with { Routes = [route with { Targets = [route.Targets[0] with { RequiresConfirmation = true }] }] };
        manifest = manifest with { ContentHash = LiveUIManifestHasher.ComputeContentHash(manifest) };
        var first = new TestConfirmation(true);
        var second = new TestConfirmation(true);
        var runtime = new RecordingRuntime();
        var orchestrator = new LiveUIOrchestrator(new RecordingManifestProvider(manifest), new RecordingAuthorizer(true), runtime,
            Options.Create(new LiveUIOptions()), new LiveUIManifestValidator(), [first, second]);

        var receipt = await orchestrator.ExecuteAsync(Action(hash: manifest.ContentHash));
        Assert.Equal("approval_required", receipt.Code);
        Assert.Equal(0, first.Calls);
        Assert.Equal(0, second.Calls);
        Assert.Equal(0, runtime.Calls);
    }

    [Fact]
    public async Task Authorization_timeout_denies_without_dispatch_even_if_authorizer_completes_late()
    {
        var authorizer = new DelayedAuthorizer();
        var runtime = new RecordingRuntime();
        var orchestrator = new LiveUIOrchestrator(new RecordingManifestProvider(Manifest), authorizer, runtime,
            Options.Create(new LiveUIOptions()), new LiveUIManifestValidator());

        var receipt = await orchestrator.ExecuteAsync(Action() with { Timeout = TimeSpan.FromMilliseconds(50) });

        Assert.Equal(LiveUIActionOutcome.Denied, receipt.Outcome);
        Assert.Equal("authorization_timeout", receipt.Code);
        Assert.Equal(LiveUIAuthorizationOutcome.TimedOut, receipt.AuthorizationOutcome);
        Assert.Equal(0, runtime.Calls);
        authorizer.Release.SetResult(new LiveUIAuthorizationDecision(true, "allowed", UserConfirmed: true));
        Assert.Equal(0, runtime.Calls);
    }

    [Fact]
    public async Task Caller_cancellation_interrupts_authorization_without_dispatch()
    {
        var authorizer = new DelayedAuthorizer();
        var runtime = new RecordingRuntime();
        var orchestrator = new LiveUIOrchestrator(new RecordingManifestProvider(Manifest), authorizer, runtime,
            Options.Create(new LiveUIOptions()), new LiveUIManifestValidator());
        using var cancellation = new CancellationTokenSource();
        var execution = orchestrator.ExecuteAsync(Action(), cancellation.Token).AsTask();
        cancellation.Cancel();
        var receipt = await execution;
        Assert.Equal(LiveUIActionOutcome.Uncertain, receipt.Outcome);
        Assert.Equal("action_cancelled", receipt.Code);
        Assert.Null(receipt.AuthorizationOutcome);
        authorizer.Release.SetResult(new LiveUIAuthorizationDecision(true, "allowed"));
        Assert.Equal(0, runtime.Calls);
    }

    private static LiveUIAction Action(string id = "action-001", string? hash = null) =>
        new(id, LiveUISchema.CurrentVersion, "session-001", hash ?? Manifest.ContentHash, "todo.home", "todo.completed",
            LiveUIActionType.SelectTab, new LiveUIActionParameters(), TimeSpan.FromSeconds(5));

    private static LiveUIManifest CreateManifest()
    {
        var manifest = new LiveUIManifest(LiveUISchema.CurrentVersion, "build-001", string.Empty,
        [
            new LiveUIRoute("todo.home", "/", "Todo", [
                new LiveUITarget("todo.completed", LiveUITargetKind.Tab, "Completed",
                    new HashSet<LiveUICapability>
                    {
                        LiveUICapability.Activate, LiveUICapability.Filter, LiveUICapability.Highlight,
                        LiveUICapability.WaitForState
                    })
            ])
        ]);
        return manifest with { ContentHash = LiveUIManifestHasher.ComputeContentHash(manifest) };
    }

    [Fact]
    public void ManifestHasher_IsStableAcrossRouteAndCapabilityOrdering()
    {
        var route = Manifest.Routes[0];
        var reordered = Manifest with
        {
            Routes = [route with
            {
                Targets = [route.Targets[0] with
                {
                    Capabilities = new HashSet<LiveUICapability>
                    {
                        LiveUICapability.Highlight, LiveUICapability.Activate, LiveUICapability.Filter,
                        LiveUICapability.WaitForState
                    }
                }]
            }]
        };

        Assert.Equal(Manifest.ContentHash, LiveUIManifestHasher.ComputeContentHash(reordered));
        Assert.Contains("manifest_hash_mismatch",
            new LiveUIManifestValidator().Validate(Manifest with { BuildId = "other-build" }));
    }

    [Fact]
    public void ManifestValidator_RejectsDuplicateIdsAndUnknownCapabilities()
    {
        var route = Manifest.Routes[0];
        var duplicate = route.Targets[0] with
        {
            Capabilities = new HashSet<LiveUICapability> { LiveUICapability.Activate }
        };
        var invalid = route.Targets[0] with
        {
            Id = "todo.invalid",
            Capabilities = new HashSet<LiveUICapability> { (LiveUICapability)999 }
        };
        var candidate = Manifest with { Routes = [route with { Targets = [route.Targets[0], duplicate, invalid] }] };

        var errors = new LiveUIManifestValidator().Validate(candidate);

        Assert.Contains("manifest_target_id_duplicate", errors);
        Assert.Contains("manifest_target_invalid", errors);
    }

    [Fact]
    public void ManifestValidator_RejectsMissingAndCyclicParentTargets()
    {
        var route = new LiveUIRoute("route.home", "/", "Home",
        [
            new LiveUITarget("target.missing-parent", LiveUITargetKind.Button, "Missing parent",
                new HashSet<LiveUICapability> { LiveUICapability.Activate }, ParentId: "target.unknown"),
            new LiveUITarget("target.cycle-a", LiveUITargetKind.Region, "Cycle A",
                new HashSet<LiveUICapability>(), ParentId: "target.cycle-b"),
            new LiveUITarget("target.cycle-b", LiveUITargetKind.Region, "Cycle B",
                new HashSet<LiveUICapability>(), ParentId: "target.cycle-a")
        ]);
        var candidate = Manifest with { Routes = [route] };

        var errors = new LiveUIManifestValidator().Validate(candidate);

        Assert.Contains("manifest_target_parent_missing", errors);
        Assert.Contains("manifest_target_parent_cycle", errors);
    }

    [Fact]
    public async Task ExecuteAsync_DeniesBeforeBrowserExecution_WhenAuthorizationFails()
    {
        var runtime = new RecordingRuntime();
        var orchestrator = Create(authorizer: new RecordingAuthorizer(false), runtime: runtime);

        var receipt = await orchestrator.ExecuteAsync(Action());

        Assert.Equal(LiveUIActionOutcome.Denied, receipt.Outcome);
        Assert.Equal("policy_denied", receipt.Code);
        Assert.Equal(LiveUIAuthorizationOutcome.Denied, receipt.AuthorizationOutcome);
        Assert.False(receipt.PostconditionVerified);
        Assert.Equal(0, runtime.Calls);
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("//external.example/path")]
    [InlineData("/docs?token=value")]
    [InlineData("/docs#section")]
    [InlineData("/docs\\other")]
    public async Task ExecuteAsync_rejects_unsafe_concrete_path_before_browser_execution(string path)
    {
        var runtime = new RecordingRuntime();
        var orchestrator = Create(authorizer: new RecordingAuthorizer(true), runtime: runtime);

        var receipt = await orchestrator.ExecuteAsync(Action() with { ExpectedPath = path });

        Assert.NotEqual(LiveUIActionOutcome.Completed, receipt.Outcome);
        Assert.Equal(0, runtime.Calls);
    }

    [Fact]
    public async Task ExecuteAsync_RequiresHostAuthorizationForEveryActionType()
    {
        var allCapabilities = Enum.GetValues<LiveUICapability>().ToHashSet();
        var route = Manifest.Routes[0] with
        {
            Targets = [Manifest.Routes[0].Targets[0] with { Capabilities = allCapabilities }]
        };
        var manifest = Manifest with { Routes = [route] };
        manifest = manifest with { ContentHash = LiveUIManifestHasher.ComputeContentHash(manifest) };
        var authorizer = new RecordingAuthorizer(false);
        var runtime = new RecordingRuntime();
        var orchestrator = new LiveUIOrchestrator(new RecordingManifestProvider(manifest), authorizer, runtime,
            Options.Create(new LiveUIOptions()), new LiveUIManifestValidator());

        foreach (var actionType in Enum.GetValues<LiveUIActionType>())
        {
            var parameters = actionType switch
            {
                LiveUIActionType.Navigate => new LiveUIActionParameters(DestinationRouteId: route.Id),
                LiveUIActionType.SetValue or LiveUIActionType.Select => new LiveUIActionParameters(Value: "value"),
                LiveUIActionType.WaitForState => new LiveUIActionParameters(StateName: "visible", StateValue: "true"),
                _ => new LiveUIActionParameters()
            };
            var targetId = actionType is LiveUIActionType.Navigate or LiveUIActionType.ClearHighlight
                ? null
                : route.Targets[0].Id;
            var action = new LiveUIAction($"action-{actionType}", LiveUISchema.CurrentVersion, "session-001",
                manifest.ContentHash, route.Id, targetId, actionType, parameters, TimeSpan.FromSeconds(5));

            var receipt = await orchestrator.ExecuteAsync(action);

            Assert.Equal(LiveUIActionOutcome.Denied, receipt.Outcome);
            Assert.Equal("policy_denied", receipt.Code);
        }

        Assert.Equal(Enum.GetValues<LiveUIActionType>().Length, authorizer.Calls);
        Assert.Equal(0, runtime.Calls);
    }

    [Fact]
    public async Task ExecuteAsync_AllActionTypesRequireAndHonorHostAuthorizationBeforeExecution()
    {
        var allCapabilities = Enum.GetValues<LiveUICapability>().ToHashSet();
        var route = Manifest.Routes[0] with
        {
            Targets = [Manifest.Routes[0].Targets[0] with { Capabilities = allCapabilities }]
        };
        var manifest = Manifest with { Routes = [route] };
        manifest = manifest with { ContentHash = LiveUIManifestHasher.ComputeContentHash(manifest) };
        var authorizer = new RecordingAuthorizer(true);
        var runtime = new RecordingRuntime();
        var orchestrator = new LiveUIOrchestrator(new RecordingManifestProvider(manifest), authorizer, runtime,
            Options.Create(new LiveUIOptions()), new LiveUIManifestValidator());

        foreach (var actionType in Enum.GetValues<LiveUIActionType>())
        {
            var parameters = actionType switch
            {
                LiveUIActionType.Navigate => new LiveUIActionParameters(DestinationRouteId: route.Id),
                LiveUIActionType.SetValue or LiveUIActionType.Select => new LiveUIActionParameters(Value: "value"),
                LiveUIActionType.WaitForState => new LiveUIActionParameters(StateName: "visible", StateValue: "true"),
                _ => new LiveUIActionParameters()
            };
            var targetId = actionType is LiveUIActionType.Navigate or LiveUIActionType.ClearHighlight
                ? null
                : route.Targets[0].Id;
            var action = new LiveUIAction($"allowed-{actionType}", LiveUISchema.CurrentVersion, "session-allowed",
                manifest.ContentHash, route.Id, targetId, actionType, parameters, TimeSpan.FromSeconds(5));

            var receipt = await orchestrator.ExecuteAsync(action);

            Assert.Equal(LiveUIActionOutcome.Completed, receipt.Outcome);
            Assert.True(receipt.PostconditionVerified);
        }

        Assert.Equal(Enum.GetValues<LiveUIActionType>().Length, authorizer.Calls);
        Assert.Equal(Enum.GetValues<LiveUIActionType>().Length, runtime.Calls);
    }

    [Fact]
    public async Task ExecuteAsync_RejectsStaleManifestBeforeAuthorization()
    {
        var authorizer = new RecordingAuthorizer(true);
        var orchestrator = Create(authorizer: authorizer);

        var receipt = await orchestrator.ExecuteAsync(Action(hash: "sha256:old"));

        Assert.Equal(LiveUIActionOutcome.Conflict, receipt.Outcome);
        Assert.Equal("manifest_stale", receipt.Code);
        Assert.Equal(0, authorizer.Calls);
        Assert.Null(receipt.AuthorizationOutcome);
    }

    [Fact]
    public async Task ExecuteAsync_RequiresVerifiedPostconditionForCompletedReceipt()
    {
        var orchestrator = Create(runtime: new RecordingRuntime(new LiveUIRuntimeResult(
            LiveUIActionOutcome.Completed, "clicked", PostconditionVerified: false)));

        var receipt = await orchestrator.ExecuteAsync(Action());

        Assert.Equal(LiveUIActionOutcome.Uncertain, receipt.Outcome);
        Assert.Equal("postcondition_unverified", receipt.Code);
        Assert.False(receipt.PostconditionVerified);
        Assert.Equal(LiveUIAuthorizationOutcome.Allowed, receipt.AuthorizationOutcome);
    }

    [Fact]
    public async Task ExecuteAsync_RequiresHostConfirmedApprovalForConsequentialTargets()
    {
        var route = Manifest.Routes[0];
        var consequential = route.Targets[0] with { RequiresConfirmation = true };
        var changedManifest = Manifest with
        {
            Routes = [route with { Targets = [consequential] }]
        };
        changedManifest = changedManifest with
        {
            ContentHash = LiveUIManifestHasher.ComputeContentHash(changedManifest)
        };
        var manifestProvider = new RecordingManifestProvider(changedManifest);
        var runtime = new RecordingRuntime();
        var orchestrator = new LiveUIOrchestrator(
            manifestProvider,
            new RecordingAuthorizer(true),
            runtime,
            Options.Create(new LiveUIOptions()),
            new LiveUIManifestValidator());

        var receipt = await orchestrator.ExecuteAsync(Action(hash: changedManifest.ContentHash));

        Assert.Equal(LiveUIActionOutcome.Denied, receipt.Outcome);
        Assert.Equal("approval_required", receipt.Code);
        Assert.Equal(0, runtime.Calls);
    }

    [Fact]
    public async Task ExecuteAsync_RespectsFilteringOptionForCompilerMarkedFilterTargets()
    {
        var route = Manifest.Routes[0];
        var filter = route.Targets[0] with
        {
            Capabilities = new HashSet<LiveUICapability>
            {
                LiveUICapability.Activate, LiveUICapability.Highlight, LiveUICapability.Filter
            }
        };
        var manifest = Manifest with { Routes = [route with { Targets = [filter] }] };
        manifest = manifest with { ContentHash = LiveUIManifestHasher.ComputeContentHash(manifest) };
        var runtime = new RecordingRuntime();
        var orchestrator = new LiveUIOrchestrator(
            new RecordingManifestProvider(manifest),
            new RecordingAuthorizer(true),
            runtime,
            Options.Create(new LiveUIOptions { Interaction = new LiveUIInteractionOptions { EnableFiltering = false } }),
            new LiveUIManifestValidator());

        var receipt = await orchestrator.ExecuteAsync(Action(hash: manifest.ContentHash));

        Assert.Equal(LiveUIActionOutcome.Denied, receipt.Outcome);
        Assert.Equal("filtering_disabled", receipt.Code);
        Assert.Equal(0, runtime.Calls);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(30_001)]
    public async Task ExecuteAsync_RejectsTemporaryHighlightDurationsOutsideTheBound(int durationMilliseconds)
    {
        var runtime = new RecordingRuntime();
        var action = Action() with
        {
            Type = LiveUIActionType.Highlight,
            Parameters = new LiveUIActionParameters(HighlightDurationMilliseconds: durationMilliseconds)
        };

        var receipt = await Create(runtime: runtime).ExecuteAsync(action);

        Assert.Equal(LiveUIActionOutcome.Failed, receipt.Outcome);
        Assert.Equal("invalid_action", receipt.Code);
        Assert.Equal(0, runtime.Calls);
    }

    [Fact]
    public async Task ExecuteAsync_RejectsHighlightDurationOnAnUnrelatedAction()
    {
        var runtime = new RecordingRuntime();
        var action = Action() with
        {
            Parameters = new LiveUIActionParameters(HighlightDurationMilliseconds: 1)
        };

        var receipt = await Create(runtime: runtime).ExecuteAsync(action);

        Assert.Equal(LiveUIActionOutcome.Failed, receipt.Outcome);
        Assert.Equal("invalid_action", receipt.Code);
        Assert.Equal(0, runtime.Calls);
    }

    [Theory]
    [InlineData("textContains", null)]
    [InlineData("textContains", " ")]
    [InlineData("textNotContains", "")]
    public async Task ExecuteAsync_RejectsEmptyTextPostconditions(string stateName, string? stateValue)
    {
        var runtime = new RecordingRuntime();
        var action = Action() with
        {
            Type = LiveUIActionType.WaitForState,
            Parameters = new LiveUIActionParameters(StateName: stateName, StateValue: stateValue)
        };

        var receipt = await Create(runtime: runtime).ExecuteAsync(action);

        Assert.Equal("invalid_action", receipt.Code);
        Assert.Equal(0, runtime.Calls);
    }

    [Fact]
    public async Task ExecuteAsync_EmitsLowCardinalityRuntimeMeasurements()
    {
        var observed = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "OpenConnect.LiveUI")
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((instrument, _, _, _) =>
            observed.AddOrUpdate(instrument.Name, 1, static (_, count) => count + 1));
        listener.SetMeasurementEventCallback<long>((instrument, _, _, _) =>
            observed.AddOrUpdate(instrument.Name, 1, static (_, count) => count + 1));
        listener.Start();

        var runtime = new RecordingRuntime(new LiveUIRuntimeResult(
            LiveUIActionOutcome.MissingTarget, "target_missing", false,
            Measurements: new LiveUIRuntimeMeasurements(12.5, 3.25)));
        var orchestrator = Create(runtime: runtime);
        var missingTarget = await orchestrator.ExecuteAsync(Action());
        var confirmedStateAction = Action("action-002") with
        {
            Type = LiveUIActionType.WaitForState,
            Parameters = new LiveUIActionParameters(StateName: "selected")
        };
        var confirmedState = await Create(runtime: new RecordingRuntime()).ExecuteAsync(confirmedStateAction);

        Assert.Equal(LiveUIActionOutcome.MissingTarget, missingTarget.Outcome);
        Assert.True(confirmedState.PostconditionVerified);
        Assert.Contains("openconnect.liveui.manifest.load.duration", observed.Keys);
        Assert.Contains("openconnect.liveui.target.resolution.duration", observed.Keys);
        Assert.Contains("openconnect.liveui.target.resolution.failures", observed.Keys);
        Assert.Contains("openconnect.liveui.browser.action.duration", observed.Keys);
        Assert.Contains("openconnect.liveui.action.execution.duration", observed.Keys);
        Assert.Contains("openconnect.liveui.confirmed.state.duration", observed.Keys);
    }

    [Fact]
    public async Task ExecuteAsync_RejectsDuplicateSessionActionId()
    {
        var runtime = new RecordingRuntime();
        var orchestrator = Create(runtime: runtime);

        var first = await orchestrator.ExecuteAsync(Action());
        var second = await orchestrator.ExecuteAsync(Action());

        Assert.Equal(LiveUIActionOutcome.Completed, first.Outcome);
        Assert.True(first.PostconditionVerified);
        Assert.Equal(LiveUIActionOutcome.Conflict, second.Outcome);
        Assert.Equal("duplicate_action", second.Code);
        Assert.Equal(1, runtime.Calls);
        Assert.Equal(1, orchestrator.ClearSession("session-001"));
    }

    [Fact]
    public async Task ClearSession_DoesNotReleaseAnActionWhileRuntimeIsExecuting()
    {
        var runtime = new BlockingRuntime();
        var orchestrator = new LiveUIOrchestrator(new RecordingManifestProvider(Manifest), new RecordingAuthorizer(true),
            runtime, Options.Create(new LiveUIOptions()), new LiveUIManifestValidator());
        var first = orchestrator.ExecuteAsync(Action()).AsTask();
        await runtime.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, orchestrator.ClearSession("session-001"));
        var duplicate = await orchestrator.ExecuteAsync(Action());
        Assert.Equal("duplicate_action", duplicate.Code);
        Assert.Equal(1, runtime.Calls);

        runtime.Release.TrySetResult();
        Assert.Equal(LiveUIActionOutcome.Completed, (await first).Outcome);
        Assert.Equal(1, orchestrator.ClearSession("session-001"));
    }

    private static LiveUIOrchestrator Create(RecordingAuthorizer? authorizer = null, RecordingRuntime? runtime = null) =>
        new(new RecordingManifestProvider(Manifest), authorizer ?? new RecordingAuthorizer(true),
            runtime ?? new RecordingRuntime(), Options.Create(new LiveUIOptions()), new LiveUIManifestValidator());

    private sealed class RecordingManifestProvider(LiveUIManifest manifest) : ILiveUIManifestProvider
    {
        public ValueTask<LiveUIManifest?> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<LiveUIManifest?>(manifest);
    }

    private sealed class ChangingManifestProvider(LiveUIManifest manifest) : ILiveUIManifestProvider
    {
        private int _calls;
        public ValueTask<LiveUIManifest?> GetCurrentAsync(CancellationToken cancellationToken = default)
        {
            if (_calls++ == 0) return ValueTask.FromResult<LiveUIManifest?>(manifest);
            var changed = manifest with { BuildId = "new-build" };
            return ValueTask.FromResult<LiveUIManifest?>(changed with { ContentHash = LiveUIManifestHasher.ComputeContentHash(changed) });
        }
    }

    private sealed class RecordingAuthorizer(bool allowed) : ILiveUIActionAuthorizer
    {
        public int Calls { get; private set; }

        public ValueTask<LiveUIAuthorizationDecision> AuthorizeAsync(
            LiveUIExecutionContext context, CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(new LiveUIAuthorizationDecision(allowed, allowed ? "allowed" : "policy_denied"));
        }
    }

    private sealed class SequencedAuthorizer(bool first, bool second) : ILiveUIActionAuthorizer
    {
        private int _calls;
        public ValueTask<LiveUIAuthorizationDecision> AuthorizeAsync(LiveUIExecutionContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new LiveUIAuthorizationDecision(_calls++ == 0 ? first : second, "policy"));
    }

    private sealed class TestConfirmation(bool result) : ILiveUIActionConfirmation
    {
        public int Calls { get; private set; }
        public string? ActionId { get; private set; }
        public ValueTask<bool> ConfirmAsync(LiveUIExecutionContext context, CancellationToken cancellationToken = default)
        {
            Calls++;
            ActionId = context.Action.ActionId;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class DelayedConfirmation : ILiveUIActionConfirmation
    {
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<bool> ConfirmAsync(LiveUIExecutionContext context, CancellationToken cancellationToken = default) => new(Release.Task);
    }

    private sealed class DelayedAuthorizer : ILiveUIActionAuthorizer
    {
        public TaskCompletionSource<LiveUIAuthorizationDecision> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<LiveUIAuthorizationDecision> AuthorizeAsync(LiveUIExecutionContext context, CancellationToken cancellationToken = default) =>
            new(Release.Task);
    }

    private sealed class RecordingRuntime(LiveUIRuntimeResult? result = null) : ILiveUIRuntime
    {
        public int Calls { get; private set; }

        public ValueTask<LiveUIRuntimeResult> ExecuteAsync(
            LiveUIExecutionContext context, CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(result ?? new LiveUIRuntimeResult(
                LiveUIActionOutcome.Completed, "verified", PostconditionVerified: true, "selected"));
        }
    }

    private sealed class BlockingRuntime : ILiveUIRuntime
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }

        public async ValueTask<LiveUIRuntimeResult> ExecuteAsync(
            LiveUIExecutionContext context, CancellationToken cancellationToken = default)
        {
            Calls++;
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new LiveUIRuntimeResult(LiveUIActionOutcome.Completed, "verified", true);
        }
    }
}
