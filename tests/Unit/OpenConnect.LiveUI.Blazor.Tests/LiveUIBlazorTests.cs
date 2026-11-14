using Microsoft.Extensions.DependencyInjection;
using OpenConnect.LiveUI;
using OpenConnect.LiveUI.Blazor;
using Xunit;

namespace OpenConnect.LiveUI.Blazor.Tests;

public sealed class LiveUIBlazorTests
{
    private static readonly LiveUIManifest Manifest = CreateManifest();

    private static LiveUIManifest CreateManifest()
    {
        var manifest = new LiveUIManifest(LiveUISchema.CurrentVersion, "build-001", string.Empty,
            [new LiveUIRoute("home", "/", "Home", [])]);
        return manifest with { ContentHash = LiveUIManifestHasher.ComputeContentHash(manifest) };
    }

    [Fact]
    public async Task ManifestState_StoresOnlyValidatedManifest()
    {
        var state = new LiveUIManifestState(new LiveUIManifestValidator());
        state.SetManifest(Manifest);

        Assert.Same(Manifest, await state.GetCurrentAsync());
        Assert.Throws<ArgumentException>(() => state.SetManifest(Manifest with { SchemaVersion = "unknown" }));
        Assert.Same(Manifest, await state.GetCurrentAsync());
    }

    [Fact]
    public async Task Registration_DeniesActionsWhenHostAuthorizationPolicyIsMissing()
    {
        var services = new ServiceCollection();
        services.AddOpenConnectLiveUIBlazor();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var state = scope.ServiceProvider.GetRequiredService<LiveUIManifestState>();
        state.SetManifest(Manifest);
        var authorizer = scope.ServiceProvider.GetRequiredService<ILiveUIActionAuthorizer>();
        var context = new LiveUIExecutionContext(Manifest, Manifest.Routes[0], null,
            new LiveUIAction("a1", LiveUISchema.CurrentVersion, "s1", Manifest.ContentHash, "home", null,
                LiveUIActionType.Inspect, new LiveUIActionParameters(), TimeSpan.FromSeconds(1)));

        var decision = await authorizer.AuthorizeAsync(context);

        Assert.False(decision.Allowed);
        Assert.Equal("authorization_policy_not_configured", decision.Code);
    }

    [Fact]
    public void ManifestValidator_RejectsExternalAndProtocolRelativeRoutePaths()
    {
        var validator = new LiveUIManifestValidator();

        Assert.Contains("manifest_route_path_invalid",
            validator.Validate(Manifest with { Routes = [Manifest.Routes[0] with { Path = "//external.test" }] }));
        Assert.Contains("manifest_route_path_invalid",
            validator.Validate(Manifest with { Routes = [Manifest.Routes[0] with { Path = "https://external.test" }] }));
    }
}
