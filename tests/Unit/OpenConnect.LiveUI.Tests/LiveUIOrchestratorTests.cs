using Microsoft.Extensions.Options;
using OpenConnect.LiveUI;
using Xunit;

namespace OpenConnect.LiveUI.Tests;

public sealed class LiveUIOrchestratorTests
{
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

    private static LiveUIAction Action(string id = "action-001", string? hash = null) =>
        new(id, LiveUISchema.CurrentVersion, "session-001", hash ?? Manifest.ContentHash, "todo.home", "todo.completed",
            LiveUIActionType.SelectTab, new LiveUIActionParameters(), TimeSpan.FromSeconds(5));

    private static LiveUIManifest CreateManifest()
    {
        var manifest = new LiveUIManifest(LiveUISchema.CurrentVersion, "build-001", string.Empty,
        [
            new LiveUIRoute("todo.home", "/", "Todo", [
                new LiveUITarget("todo.completed", LiveUITargetKind.Tab, "Completed",
                    new HashSet<LiveUICapability> { LiveUICapability.Activate, LiveUICapability.Filter, LiveUICapability.Highlight })
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
                    Capabilities = new HashSet<LiveUICapability> { LiveUICapability.Highlight, LiveUICapability.Activate, LiveUICapability.Filter }
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
        Assert.False(receipt.PostconditionVerified);
        Assert.Equal(0, runtime.Calls);
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

    private static LiveUIOrchestrator Create(RecordingAuthorizer? authorizer = null, RecordingRuntime? runtime = null) =>
        new(new RecordingManifestProvider(Manifest), authorizer ?? new RecordingAuthorizer(true),
            runtime ?? new RecordingRuntime(), Options.Create(new LiveUIOptions()), new LiveUIManifestValidator());

    private sealed class RecordingManifestProvider(LiveUIManifest manifest) : ILiveUIManifestProvider
    {
        public ValueTask<LiveUIManifest?> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<LiveUIManifest?>(manifest);
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
}
