using Microsoft.Extensions.Options;
using OpenConnect.LiveUI;
using Xunit;

namespace OpenConnect.LiveUI.Tests;

public sealed class LiveUITenantIsolationTests
{
    [Fact]
    public async Task Orchestrator_denies_every_action_when_session_belongs_to_another_tenant()
    {
        var manifest = CreateManifest();
        var sessionTenants = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["tenant-a-session"] = "tenant-a",
            ["tenant-b-session"] = "tenant-b"
        };
        var authorizer = new TenantOwnershipAuthorizer("tenant-a", sessionTenants);
        var runtime = new RecordingRuntime();
        var orchestrator = new LiveUIOrchestrator(new ManifestProvider(manifest), authorizer, runtime,
            Options.Create(new LiveUIOptions()), new LiveUIManifestValidator());
        var route = manifest.Routes[0];

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
            var action = new LiveUIAction($"tenant-b-{actionType}", LiveUISchema.CurrentVersion, "tenant-b-session",
                manifest.ContentHash, route.Id, targetId, actionType, parameters, TimeSpan.FromSeconds(5));

            var receipt = await orchestrator.ExecuteAsync(action);

            Assert.Equal(LiveUIActionOutcome.Denied, receipt.Outcome);
            Assert.Equal("policy_denied", receipt.Code);
        }

        Assert.Equal(Enum.GetValues<LiveUIActionType>().Length, authorizer.Contexts.Count);
        Assert.All(authorizer.Contexts, context => Assert.Equal("tenant-b-session", context.Action.SessionId));
        Assert.Equal(0, runtime.Calls);
    }

    private static LiveUIManifest CreateManifest()
    {
        var manifest = new LiveUIManifest(LiveUISchema.CurrentVersion, "tenant-test-build", string.Empty,
        [
            new LiveUIRoute("todo.home", "/", "Todo",
            [
                new LiveUITarget("todo.target", LiveUITargetKind.Tab, "Completed",
                    Enum.GetValues<LiveUICapability>().ToHashSet())
            ])
        ]);
        return manifest with { ContentHash = LiveUIManifestHasher.ComputeContentHash(manifest) };
    }

    private sealed class TenantOwnershipAuthorizer(
        string authenticatedTenant,
        IReadOnlyDictionary<string, string> sessionTenants) : ILiveUIActionAuthorizer
    {
        public List<LiveUIExecutionContext> Contexts { get; } = [];

        public ValueTask<LiveUIAuthorizationDecision> AuthorizeAsync(
            LiveUIExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Contexts.Add(context);
            var allowed = sessionTenants.TryGetValue(context.Action.SessionId, out var ownerTenant) &&
                          string.Equals(ownerTenant, authenticatedTenant, StringComparison.Ordinal);
            return ValueTask.FromResult(new LiveUIAuthorizationDecision(allowed, allowed ? "allowed" : "policy_denied"));
        }
    }

    private sealed class ManifestProvider(LiveUIManifest manifest) : ILiveUIManifestProvider
    {
        public ValueTask<LiveUIManifest?> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<LiveUIManifest?>(manifest);
    }

    private sealed class RecordingRuntime : ILiveUIRuntime
    {
        public int Calls { get; private set; }

        public ValueTask<LiveUIRuntimeResult> ExecuteAsync(
            LiveUIExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(new LiveUIRuntimeResult(
                LiveUIActionOutcome.Completed, "verified", PostconditionVerified: true));
        }
    }
}
