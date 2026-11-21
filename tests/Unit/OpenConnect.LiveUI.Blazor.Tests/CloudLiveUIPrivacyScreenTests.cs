using OpenConnect.Abstractions;
using OpenConnect.LiveUI.Inference;
using OpenConnect.Privacy;
using Xunit;

namespace OpenConnect.LiveUI.Blazor.Tests;

public sealed class CloudLiveUIPrivacyScreenTests
{
    [Fact]
    public async Task ScreensEveryTransmittedFieldAndSnapshotsMutableCapabilities()
    {
        var context = Context();
        var actions = new List<string> { "activate" };
        var capabilities = new List<LiveUIInferenceCapability> { new("item", actions) { Label = "Open" } };
        var request = Request(context, capabilities);
        var checkedFields = new List<string>();
        var gate = new Gate((content, _) =>
        {
            checkedFields.Add(content);
            if (checkedFields.Count == 1) { actions[0] = "delete"; capabilities.Clear(); }
            return Allow(content == "private intent" ? "safe intent" : content);
        });

        var prepared = await CloudLiveUIPrivacyScreen.PrepareAsync(request, gate, context);

        Assert.Equal("safe intent", prepared.Intent);
        Assert.Equal("activate", Assert.Single(Assert.Single(prepared.Capabilities).Actions));
        Assert.Equal(["private intent", "v1", "safe intent", "/items", "item", "activate", "Open",
            "v1safe intent/itemsitemactivateOpen"], checkedFields);
    }

    [Fact]
    public async Task RejectsCrossFieldDisclosureAndNeverReturnsPreparedRequest()
    {
        var context = Context();
        var request = Request(context, [new("item", ["activate"]) { Label = "Open" }]);
        var gate = new Gate((content, _) => content == "v1private intent/itemsitemactivateOpen"
            ? Allow("redacted") : Allow(content));
        await Assert.ThrowsAsync<PrivacyBlockedException>(async () =>
            await CloudLiveUIPrivacyScreen.PrepareAsync(request, gate, context));
    }

    [Fact]
    public async Task RejectsWrongSessionOrDestinationBeforePrivacyInspection()
    {
        var context = Context();
        var gate = new Gate((_, _) => throw new InvalidOperationException("must not inspect"));
        var request = Request(context, []);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await CloudLiveUIPrivacyScreen.PrepareAsync(request with { SessionId = "other" }, gate, context));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await CloudLiveUIPrivacyScreen.PrepareAsync(request, gate, context with { Destination = EgressDestination.Worker }));
    }

    [Fact]
    public async Task DeniedOrMissingSanitizationFailsClosed()
    {
        var context = Context();
        var request = Request(context, []);
        foreach (var decision in new[] { new EgressDecision(false, null, [], SecurityPolicyAction.Block, "blocked"),
                     new EgressDecision(true, null, [], SecurityPolicyAction.Allow, "invalid") })
        {
            await Assert.ThrowsAsync<PrivacyBlockedException>(async () =>
                await CloudLiveUIPrivacyScreen.PrepareAsync(request, new Gate((_, _) => decision), context));
        }
    }

    [Fact]
    public async Task RejectsUnboundedActionMetadataBeforeInspection()
    {
        var context = Context();
        var request = Request(context, [new("item", Enumerable.Repeat("activate", 257).ToArray())]);
        var gate = new Gate((_, _) => throw new InvalidOperationException("must not inspect"));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await CloudLiveUIPrivacyScreen.PrepareAsync(request, gate, context));
    }

    private static EgressContext Context() => new(new TenantId(Ulid.NewUlid()), new PrincipalId(Ulid.NewUlid()),
        new SessionId(Ulid.NewUlid()), EgressDestination.ExternalModel);

    private static LiveUIInferenceRequest Request(EgressContext context, IReadOnlyList<LiveUIInferenceCapability> capabilities) =>
        new("private intent", context.Session.Value.ToString(), "/items", "v1", capabilities);

    private static EgressDecision Allow(string content) => new(true, content, [], SecurityPolicyAction.Allow, "allowed");

    private sealed class Gate(Func<string, EgressContext, EgressDecision> evaluate) : IEgressGate
    {
        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(evaluate(content, context));
        }
    }
}
