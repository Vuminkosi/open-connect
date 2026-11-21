using OpenConnect.LiveUI;
using OpenConnect.LiveUI.Agents;
using OpenConnect.LiveUI.Blazor;
using OpenConnect.LiveUI.Inference;
using Xunit;

namespace OpenConnect.LiveUI.Blazor.Tests;

public sealed class LiveUIInferenceCapabilitiesTests
{
    [Fact]
    public void BuildsAndRevalidatesOnlyCurrentAuthorizedCapabilities()
    {
        var page = Page();
        var capabilities = LiveUIInferenceCapabilities.FromPage(page);
        Assert.Equal(["activate", "inspect"], capabilities.Single(item => item.TargetId == "item").Actions.Order().ToArray());
        Assert.Equal("navigate", Assert.Single(capabilities.Single(item => item.TargetId == "other").Actions));
        var request = new LiveUIInferenceRequest("open", "session", "current", "v1", capabilities, "hash");
        Assert.True(LiveUIInferenceCapabilities.IsAuthorized(request, page));
        Assert.True(LiveUIInferenceCapabilities.IsAuthorized(request with
        {
            Capabilities = [new("item", ["activate"]) { Label = "Open" }]
        }, page));
        Assert.False(LiveUIInferenceCapabilities.IsAuthorized(request with { RouteId = "other" }, page));
        Assert.False(LiveUIInferenceCapabilities.IsAuthorized(request with { ManifestHash = "stale" }, page));
        Assert.False(LiveUIInferenceCapabilities.IsAuthorized(request with { Capabilities = [new("item", ["delete"]) { Label = "Open" }] }, page));
        Assert.False(LiveUIInferenceCapabilities.IsAuthorized(request with { Capabilities = [new("item", ["activate"]) { Label = "forged" }] }, page));
        Assert.False(LiveUIInferenceCapabilities.IsAuthorized(request with { Capabilities = [new("missing", ["activate"])] }, page));
        Assert.False(LiveUIInferenceCapabilities.IsAuthorized(request with { Capabilities = [capabilities[0], capabilities[0]] }, page));
    }

    private static LiveUIAgentPage Page() => new("current", "/items", "Items", "build", "hash",
        [new("item", "Button", "Open", new HashSet<LiveUICapability> { LiveUICapability.Activate, LiveUICapability.Inspect }, false)])
    { NavigableRoutes = [new("other", "/other", "Other")] };
}
