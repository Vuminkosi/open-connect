using OpenConnect.Docs.Services;
using OpenConnect.LiveUI;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class CloudAssistantLiveUITests
{
    [Fact]
    public void ManifestIncludesCloudOverviewGuidanceRouteWithValidContentHash()
    {
        var manifest = DocumentationLiveUIManifest.Create();

        Assert.Empty(new LiveUIManifestValidator().Validate(manifest));
        Assert.False(string.IsNullOrWhiteSpace(manifest.ContentHash));
        Assert.Equal(LiveUIManifestHasher.ComputeContentHash(manifest), manifest.ContentHash);

        var cloudRoute = Assert.Single(manifest.Routes, route => route.Path == "/cloud");
        Assert.Equal("cloud-overview", cloudRoute.Id);
        Assert.Equal("Cloud dashboard", cloudRoute.Title);

        var heading = Assert.Single(cloudRoute.Targets, target => target.Id == "cloud-overview-heading");
        Assert.Equal(LiveUITargetKind.Heading, heading.Kind);
        Assert.Equal("main h1", heading.Selector);
        Assert.Equal(
            new HashSet<LiveUICapability> { LiveUICapability.Inspect, LiveUICapability.Highlight, LiveUICapability.Scroll },
            heading.Capabilities);

        foreach (var target in cloudRoute.Targets)
        {
            Assert.Equal(LiveUITargetKind.Heading, target.Kind);
            Assert.DoesNotContain(LiveUICapability.Activate, target.Capabilities);
            Assert.DoesNotContain(LiveUICapability.SetValue, target.Capabilities);
            Assert.DoesNotContain(LiveUICapability.Select, target.Capabilities);
            Assert.DoesNotContain(LiveUICapability.Focus, target.Capabilities);
        }
    }

    [Fact]
    public async Task AuthorizerAllowsReadOnlyGuidanceOnCloudOverviewAndCrossNavigation()
    {
        var manifest = DocumentationLiveUIManifest.Create();
        var docs = manifest.Routes.Single(r => r.Path == "/docs");
        var api = manifest.Routes.Single(r => r.Path == "/api");
        var cloud = manifest.Routes.Single(r => r.Path == "/cloud");
        var heading = cloud.Targets.Single(t => t.Id == "cloud-overview-heading");
        var region = new LiveUITarget("cloud-region", LiveUITargetKind.Region, "Organizations region",
            new HashSet<LiveUICapability> { LiveUICapability.Inspect, LiveUICapability.Highlight, LiveUICapability.Scroll },
            Selector: "main section");
        var link = new LiveUITarget("cloud-docs-link", LiveUITargetKind.Link, "Documentation link",
            new HashSet<LiveUICapability> { LiveUICapability.Inspect, LiveUICapability.Highlight, LiveUICapability.Scroll },
            Selector: "header nav a[href='/docs']");
        var authorizer = new DocumentationLiveUIActionAuthorizer();

        LiveUIAction Action(string routeId, string? targetId, LiveUIActionType type, LiveUIActionParameters? parameters = null) =>
            new("action-1", LiveUISchema.CurrentVersion, "session-1", manifest.ContentHash,
                routeId, targetId, type, parameters ?? new(), TimeSpan.FromSeconds(1));

        // Navigate between SDK docs and /cloud overview.
        Assert.True((await authorizer.AuthorizeAsync(new(manifest, docs, null,
            Action(docs.Id, null, LiveUIActionType.Navigate, new(DestinationRouteId: cloud.Id))))).Allowed);
        Assert.True((await authorizer.AuthorizeAsync(new(manifest, cloud, null,
            Action(cloud.Id, null, LiveUIActionType.Navigate, new(DestinationRouteId: docs.Id))))).Allowed);
        Assert.True((await authorizer.AuthorizeAsync(new(manifest, cloud, null,
            Action(cloud.Id, null, LiveUIActionType.Navigate, new(DestinationRouteId: api.Id))))).Allowed);

        // Route-level read-only actions on /cloud.
        Assert.True((await authorizer.AuthorizeAsync(new(manifest, cloud, null,
            Action(cloud.Id, null, LiveUIActionType.Inspect)))).Allowed);
        Assert.True((await authorizer.AuthorizeAsync(new(manifest, cloud, null,
            Action(cloud.Id, null, LiveUIActionType.ClearHighlight)))).Allowed);

        // Target-level read-only actions on heading, region, and link targets on /cloud.
        foreach (var target in new[] { heading, region, link })
        {
            foreach (var actionType in new[] { LiveUIActionType.Inspect, LiveUIActionType.Highlight, LiveUIActionType.Scroll })
            {
                var decision = await authorizer.AuthorizeAsync(new(manifest, cloud, target,
                    Action(cloud.Id, target.Id, actionType)));
                Assert.True(decision.Allowed, $"Expected {actionType} on {target.Kind} to be allowed on /cloud.");
            }
        }
    }

    [Theory]
    [InlineData(LiveUIActionType.Click)]
    [InlineData(LiveUIActionType.SetValue)]
    [InlineData(LiveUIActionType.Select)]
    [InlineData(LiveUIActionType.Focus)]
    public async Task AuthorizerStrictlyDeniesStateChangingAndInteractiveActionsOnCloud(LiveUIActionType disallowedType)
    {
        var manifest = DocumentationLiveUIManifest.Create();
        var cloud = manifest.Routes.Single(r => r.Path == "/cloud");
        var heading = cloud.Targets.Single(t => t.Id == "cloud-overview-heading");
        var button = new LiveUITarget("cloud-create-tenant", LiveUITargetKind.Button, "Create organization",
            new HashSet<LiveUICapability> { LiveUICapability.Activate }, Selector: "button[type='submit']");
        var input = new LiveUITarget("cloud-tenant-name", LiveUITargetKind.Input, "Organization name",
            new HashSet<LiveUICapability> { LiveUICapability.SetValue }, Selector: "input[name='name']");
        var select = new LiveUITarget("cloud-role", LiveUITargetKind.Select, "Role",
            new HashSet<LiveUICapability> { LiveUICapability.Select }, Selector: "select[name='role']");
        var authorizer = new DocumentationLiveUIActionAuthorizer();

        foreach (var target in new LiveUITarget?[] { null, heading, button, input, select })
        {
            var action = new LiveUIAction("action-1", LiveUISchema.CurrentVersion, "session-1", manifest.ContentHash,
                cloud.Id, target?.Id, disallowedType, new(Value: "test"), TimeSpan.FromSeconds(1));
            var decision = await authorizer.AuthorizeAsync(new(manifest, cloud, target, action));
            Assert.False(decision.Allowed, $"Expected {disallowedType} on target {target?.Id ?? "<null>"} to be denied on /cloud.");
        }
    }

    [Fact]
    public async Task AuthorizerDeniesInteractiveTargetInspectionAndCloudAdminSubRoutes()
    {
        var manifest = DocumentationLiveUIManifest.Create();
        var cloud = manifest.Routes.Single(r => r.Path == "/cloud");
        var button = new LiveUITarget("cloud-sign-out", LiveUITargetKind.Button, "Sign out",
            new HashSet<LiveUICapability> { LiveUICapability.Inspect }, Selector: "button[type='submit']");
        var input = new LiveUITarget("cloud-api-key", LiveUITargetKind.Input, "API key",
            new HashSet<LiveUICapability> { LiveUICapability.Inspect }, Selector: "input");
        var authorizer = new DocumentationLiveUIActionAuthorizer();

        // Even read-only actions must not target interactive form controls on /cloud.
        foreach (var target in new[] { button, input })
        {
            foreach (var actionType in new[] { LiveUIActionType.Inspect, LiveUIActionType.Highlight, LiveUIActionType.Scroll })
            {
                var action = new LiveUIAction("action-1", LiveUISchema.CurrentVersion, "session-1", manifest.ContentHash,
                    cloud.Id, target.Id, actionType, new(), TimeSpan.FromSeconds(1));
                Assert.False((await authorizer.AuthorizeAsync(new(manifest, cloud, target, action))).Allowed);
            }
        }

        // Unreviewed /cloud/* administration sub-routes must remain completely denied.
        var adminRoute = new LiveUIRoute("cloud-operator", "/cloud/operator", "Operator", []);
        var extendedManifest = manifest with { Routes = manifest.Routes.Append(adminRoute).ToArray() };
        var navigateToAdmin = new LiveUIAction("action-1", LiveUISchema.CurrentVersion, "session-1", manifest.ContentHash,
            cloud.Id, null, LiveUIActionType.Navigate, new(DestinationRouteId: adminRoute.Id), TimeSpan.FromSeconds(1));
        Assert.False((await authorizer.AuthorizeAsync(new(extendedManifest, cloud, null, navigateToAdmin))).Allowed);

        var inspectAdmin = new LiveUIAction("action-2", LiveUISchema.CurrentVersion, "session-1", manifest.ContentHash,
            adminRoute.Id, null, LiveUIActionType.Inspect, new(), TimeSpan.FromSeconds(1));
        Assert.False((await authorizer.AuthorizeAsync(new(extendedManifest, adminRoute, null, inspectAdmin))).Allowed);
    }
}
