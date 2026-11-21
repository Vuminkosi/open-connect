using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Client;
using OpenConnect.Components.Blazor;
using OpenConnect.Docs.Services;
using OpenConnect.Docs.Services.Voice;
using OpenConnect.LiveUI;
using OpenConnect.LiveUI.Inference;
using Microsoft.Extensions.Options;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class DocumentationAssistantTests
{
    [Theory]
    [InlineData("https://localhost:8098/", true)]
    [InlineData("https://gateway.example/", false)]
    public void LocalVoiceReadinessRequiresLoopbackVisitorHost(string gateway, bool valid)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OpenConnect:Assistant:Enabled"] = "true",
            ["OpenConnect:Assistant:GatewayUrl"] = gateway,
            ["OpenConnect:Assistant:VisitorTenant"] = "01ARZ3NDEKTSV4RRFFQ69G5FAV",
            ["OpenConnect:Assistant:VisitorSigningKey"] = Convert.ToBase64String(new byte[32]),
            ["OpenConnect:Assistant:LocalVoiceReadinessEnabled"] = "true"
        });
        if (!valid)
        {
            Assert.Throws<InvalidOperationException>(() => builder.Services.AddDocumentationAssistant(builder.Configuration, builder.Environment));
            return;
        }
        builder.Services.AddDocumentationAssistant(builder.Configuration, builder.Environment);
        Assert.Contains(builder.Services, service => service.ServiceType == typeof(DocumentationVoiceAvailability));
    }

    [Fact]
    public async Task PrivacyExampleCanBeTriggeredOnlyOnItsPublicGuide()
    {
        var content = new DocumentationContentStore();
        content.Load();
        var manifest = DocumentationLiveUIManifest.Create(content.Documents);
        Assert.Empty(new LiveUIManifestValidator().Validate(manifest));
        var route = manifest.Routes.Single(item => item.Path == "/docs/v1/privacy");
        var target = route.Targets.Single(item => item.Id == "documentation-privacy-demo-run");
        var action = new LiveUIAction("action", LiveUISchema.CurrentVersion, "session", manifest.ContentHash,
            route.Id, target.Id, LiveUIActionType.Click, new(), TimeSpan.FromSeconds(1));
        var authorizer = new DocumentationLiveUIActionAuthorizer(content);
        Assert.True((await authorizer.AuthorizeAsync(new(manifest, route, target, action))).Allowed);
        Assert.False((await authorizer.AuthorizeAsync(new(manifest, route with { Path = "/cloud" }, target, action))).Allowed);
        Assert.False((await authorizer.AuthorizeAsync(new(manifest, route, target with { Id = "other" }, action))).Allowed);
        var select = route.Targets.Single(item => item.Id == "documentation-privacy-demo-example");
        var selection = action with { TargetId = select.Id, Type = LiveUIActionType.Select, Parameters = new(Value: "ordinary") };
        Assert.True((await authorizer.AuthorizeAsync(new(manifest, route, select, selection))).Allowed);
        Assert.True((await authorizer.AuthorizeAsync(new(manifest, route, select,
            selection with { Parameters = new(Value: "email") }))).Allowed);
        Assert.True((await authorizer.AuthorizeAsync(new(manifest, route, select,
            selection with { Parameters = new() }))).Allowed);
        Assert.False((await authorizer.AuthorizeAsync(new(manifest, route, select,
            selection with { Parameters = new(Value: "arbitrary") }))).Allowed);
        Assert.False((await authorizer.AuthorizeAsync(new(manifest, route with { Path = "/cloud" }, select, selection))).Allowed);
    }

    [Fact]
    public async Task ApiSearchIsAdvertisedOnlyWhenPresentAndRemainsBounded()
    {
        var empty = DocumentationLiveUIManifest.Create();
        Assert.DoesNotContain(empty.Routes.Single(route => route.Path == "/api").Targets,
            target => target.Id == "documentation-api-search");
        var manifest = DocumentationLiveUIManifest.Create(hasGeneratedApiDocuments: true);
        Assert.Empty(new LiveUIManifestValidator().Validate(manifest));
        var route = manifest.Routes.Single(item => item.Path == "/api");
        var target = route.Targets.Single(item => item.Id == "documentation-api-search");
        Assert.Equal("#api-search", target.Selector);
        var action = new LiveUIAction("action", LiveUISchema.CurrentVersion, "session", manifest.ContentHash,
            route.Id, target.Id, LiveUIActionType.SetValue, new(Value: "OpenConnect"), TimeSpan.FromSeconds(1));
        var authorizer = new DocumentationLiveUIActionAuthorizer();
        Assert.True((await authorizer.AuthorizeAsync(new(manifest, route, target, action))).Allowed);
        Assert.False((await authorizer.AuthorizeAsync(new(manifest, route, target,
            action with { Parameters = new(Value: new string('a', 201)) }))).Allowed);
        Assert.False((await authorizer.AuthorizeAsync(new(manifest, route with { Path = "/cloud" }, target, action))).Allowed);
        Assert.False((await authorizer.AuthorizeAsync(new(manifest, route, target,
            action with { Type = LiveUIActionType.Click }))).Allowed);
    }

    [Fact]
    public async Task ApiReferenceContractSectionSupportsReadOnlyGuidance()
    {
        var manifest = DocumentationLiveUIManifest.Create();
        Assert.Empty(new LiveUIManifestValidator().Validate(manifest));
        var route = manifest.Routes.Single(item => item.Path == "/api");
        var target = route.Targets.Single(item => item.Id == "documentation-api-rest-worker-contracts");
        Assert.Equal("#api-rest-worker-contracts", target.Selector);
        var action = new LiveUIAction("action", LiveUISchema.CurrentVersion, "session", manifest.ContentHash,
            route.Id, target.Id, LiveUIActionType.Scroll, new(), TimeSpan.FromSeconds(1));
        var authorizer = new DocumentationLiveUIActionAuthorizer();
        Assert.True((await authorizer.AuthorizeAsync(new(manifest, route, target, action))).Allowed);
        Assert.False((await authorizer.AuthorizeAsync(new(manifest, route, target,
            action with { Type = LiveUIActionType.Click }))).Allowed);
    }

    [Fact]
    public async Task DocumentationGuideShowcaseIsTheOnlyAuthorizedButton()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddOpenConnectComponents();
        builder.Services.AddDocumentationAssistant(builder.Configuration, builder.Environment);
        using var services = builder.Services.BuildServiceProvider();
        using var scope = services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<OpenConnect.Components.Blazor.OpenConnectComponentRegistry>();
        foreach (var registration in scope.ServiceProvider.GetServices<OpenConnect.Components.Blazor.IConfigureOpenConnectComponent>())
            registration.Apply(registry);
        Assert.True(registry.IsRegistered("documentation-guides"));

        var manifest = DocumentationLiveUIManifest.Create();
        Assert.Empty(new LiveUIManifestValidator().Validate(manifest));
        var route = manifest.Routes.Single(item => item.Path == "/docs");
        var target = route.Targets.Single(item => item.Id == "documentation-show-guides");
        var action = new LiveUIAction("action", LiveUISchema.CurrentVersion, "session", manifest.ContentHash,
            route.Id, target.Id, LiveUIActionType.Click, new(), TimeSpan.FromSeconds(1));
        var authorizer = new DocumentationLiveUIActionAuthorizer();
        Assert.True((await authorizer.AuthorizeAsync(new(manifest, route, target, action))).Allowed);
        Assert.False((await authorizer.AuthorizeAsync(new(manifest, route, target with { Id = "other" }, action))).Allowed);
        Assert.False((await authorizer.AuthorizeAsync(new(manifest, route with { Path = "/cloud" }, target, action))).Allowed);
        Assert.False((await authorizer.AuthorizeAsync(new(manifest, route, target, action with { Type = LiveUIActionType.SetValue }))).Allowed);
    }

    [Fact]
    public async Task LiveUIExposesOnlyRenderedDocumentationSectionsForReadOnlyGuidance()
    {
        const string markdown = "# Guide\n## Install\n### Configure\n## Install\n```text\n## Hidden code\n```";
        var document = new DocumentationDocument("v1", "guide", "Guide", "", 0, markdown, []);
        var manifest = DocumentationLiveUIManifest.Create([document]);
        Assert.Empty(new LiveUIManifestValidator().Validate(manifest));
        var route = manifest.Routes.Single(item => item.Path == "/docs/v1/guide");
        Assert.Equal(4, route.Targets.Count);
        Assert.Equal(new[] { ".markdown-body [id=\"install\"]", ".markdown-body [id=\"configure\"]",
            ".markdown-body [id=\"install-1\"]" }, route.Targets.Skip(1).Select(target => target.Selector));
        Assert.DoesNotContain(route.Targets, target => target.Label == "Hidden code");

        var content = new DocumentationContentStore();
        content.Load();
        var realManifest = DocumentationLiveUIManifest.Create(content.Documents);
        var realRoute = realManifest.Routes.First(item => item.Path.StartsWith("/docs/v1/", StringComparison.Ordinal) && item.Targets.Count > 1);
        var target = realRoute.Targets[1];
        var authorizer = new DocumentationLiveUIActionAuthorizer(content);
        var action = new LiveUIAction("action", LiveUISchema.CurrentVersion, "session", realManifest.ContentHash,
            realRoute.Id, target.Id, LiveUIActionType.Scroll, new(), TimeSpan.FromSeconds(1));
        Assert.True((await authorizer.AuthorizeAsync(new(realManifest, realRoute, target, action))).Allowed);
        Assert.False((await authorizer.AuthorizeAsync(new(realManifest, realRoute, target,
            action with { Type = LiveUIActionType.SetValue }))).Allowed);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public void LiveUIWorkerRequiresBothGatewayAndExplicitOptIn(bool gateway, bool enabled, bool expected)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
            ["OpenConnect:Assistant:Enabled"] = gateway.ToString(), ["OpenConnect:Assistant:GatewayUrl"] = "https://gateway.example/",
            ["OpenConnect:Assistant:LiveUIWorkerEnabled"] = enabled.ToString() });
        builder.Services.AddDocumentationAssistant(builder.Configuration, builder.Environment);
        using var provider = builder.Services.BuildServiceProvider();
        Assert.Equal(expected, provider.GetRequiredService<IOptions<LiveUIInferenceOptions>>().Value.Worker.Enabled);
    }

    [Fact]
    public void ConfiguredApplicationCredentialIsAppliedToGatewayRequests()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
            ["OpenConnect:Assistant:Enabled"] = "true", ["OpenConnect:Assistant:GatewayUrl"] = "https://gateway.example/",
            ["OpenConnect:Assistant:GatewayBearer"] = "test-token" });
        builder.Services.AddDocumentationAssistant(builder.Configuration, builder.Environment);
        using var services = builder.Services.BuildServiceProvider();
        var client = services.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(IOpenConnectClient));
        Assert.Equal("Bearer", client.DefaultRequestHeaders.Authorization?.Scheme);
        Assert.Equal("test-token", client.DefaultRequestHeaders.Authorization?.Parameter);
    }

    [Theory]
    [InlineData("bad token")]
    [InlineData("bad\ntoken")]
    public void RejectsMalformedApplicationCredentials(string token)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
            ["OpenConnect:Assistant:Enabled"] = "true", ["OpenConnect:Assistant:GatewayUrl"] = "https://gateway.example/",
            ["OpenConnect:Assistant:GatewayBearer"] = token });
        Assert.Throws<InvalidOperationException>(() => builder.Services.AddDocumentationAssistant(builder.Configuration, builder.Environment));
    }

    [Theory]
    [InlineData("http://localhost:5001/", true)]
    [InlineData("http://gateway.example/", false)]
    public void PlaintextCredentialsAreRestrictedToDevelopmentLoopback(string gateway, bool allowed)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
            ["OpenConnect:Assistant:Enabled"] = "true", ["OpenConnect:Assistant:GatewayUrl"] = gateway,
            ["OpenConnect:Assistant:GatewayBearer"] = "test-token" });
        if (allowed)
            builder.Services.AddDocumentationAssistant(builder.Configuration, builder.Environment);
        else
            Assert.Throws<InvalidOperationException>(() => builder.Services.AddDocumentationAssistant(builder.Configuration, builder.Environment));
    }

    [Fact]
    public void ConfiguredComposerUsesApplicationGatewayAndRejectsCloudCapabilityRoutes()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
            ["OpenConnect:Assistant:Enabled"] = "true", ["OpenConnect:Assistant:GatewayUrl"] = "https://gateway.example/" });
        builder.Services.AddDocumentationAssistant(builder.Configuration, builder.Environment);
        using var services = builder.Services.BuildServiceProvider();
        Assert.True(services.GetRequiredService<DocumentationAssistantAvailability>().Configured);
        Assert.Equal("https://gateway.example/openconnect/v1/", services.GetRequiredService<OpenConnectClientOptions>().ApiBaseAddress!.ToString());
        var invalid = WebApplication.CreateBuilder();
        invalid.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
            ["OpenConnect:Assistant:Enabled"] = "true", ["OpenConnect:Assistant:GatewayUrl"] = "https://gateway.example/cloud/v1/" });
        Assert.Throws<InvalidOperationException>(() => invalid.Services.AddDocumentationAssistant(invalid.Configuration, invalid.Environment));
    }

    [Fact]
    public async Task UnconfiguredComposerFailsClosedWithoutConnectingToAnExternalGateway()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddDocumentationAssistant(builder.Configuration, builder.Environment);
        await using var services = builder.Services.BuildServiceProvider();
        Assert.False(services.GetRequiredService<DocumentationAssistantAvailability>().Configured);
        var client = services.GetRequiredService<IOpenConnectClient>();
        var exception = await Assert.ThrowsAsync<OpenConnectClientException>(() => client.CreateSessionAsync());
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, exception.StatusCode);
        Assert.IsType<DocumentationLiveUIActionAuthorizer>(services.CreateScope().ServiceProvider.GetRequiredService<ILiveUIActionAuthorizer>());
    }

    [Fact]
    public async Task LiveUIAllowsReviewedPublicNavigationButDeniesCloudAndMutations()
    {
        var manifest = DocumentationLiveUIManifest.Create();
        Assert.Empty(new LiveUIManifestValidator().Validate(manifest));
        var home = manifest.Routes.Single(x => x.Path == "/");
        var docs = manifest.Routes.Single(x => x.Path == "/docs");
        var authorizer = new DocumentationLiveUIActionAuthorizer();
        var action = new LiveUIAction("action", LiveUISchema.CurrentVersion, "session", manifest.ContentHash,
            home.Id, null, LiveUIActionType.Navigate, new(DestinationRouteId: docs.Id), TimeSpan.FromSeconds(1));
        Assert.True((await authorizer.AuthorizeAsync(new(manifest, home, null, action))).Allowed);
        var cloud = new LiveUIRoute("cloud", "/cloud", "Cloud", []);
        var cloudManifest = manifest with { Routes = manifest.Routes.Append(cloud).ToArray() };
        Assert.False((await authorizer.AuthorizeAsync(new(cloudManifest, home, null,
            action with { Parameters = new(DestinationRouteId: cloud.Id) }))).Allowed);
        Assert.False((await authorizer.AuthorizeAsync(new(manifest, home, null, action with { Type = LiveUIActionType.Click }))).Allowed);
        Assert.False((await authorizer.AuthorizeAsync(new(cloudManifest, cloud, null, action with { Type = LiveUIActionType.Inspect }))).Allowed);
    }

    [Fact]
    public async Task LiveUIExposesLoadedDocumentationAndRejectsUnknownRoutes()
    {
        var content = new DocumentationContentStore();
        content.Load();
        var manifest = DocumentationLiveUIManifest.Create(content.Documents);
        Assert.Empty(new LiveUIManifestValidator().Validate(manifest));
        var home = manifest.Routes.Single(route => route.Path == "/");
        var document = content.Documents.First();
        var destination = manifest.Routes.Single(route => route.Path == $"/docs/{document.Version}/{document.Slug}");
        var action = new LiveUIAction("action", LiveUISchema.CurrentVersion, "session", manifest.ContentHash,
            home.Id, null, LiveUIActionType.Navigate, new(DestinationRouteId: destination.Id), TimeSpan.FromSeconds(1));
        var authorizer = new DocumentationLiveUIActionAuthorizer(content);
        Assert.True((await authorizer.AuthorizeAsync(new(manifest, home, null, action))).Allowed);
        var unknown = new LiveUIRoute("unknown", "/docs/v1/not-a-document", "Unknown", []);
        var extended = manifest with { Routes = manifest.Routes.Append(unknown).ToArray() };
        Assert.False((await authorizer.AuthorizeAsync(new(extended, home, null,
            action with { Parameters = new(DestinationRouteId: unknown.Id) }))).Allowed);
    }

    [Fact]
    public async Task LiveUIDocumentationSearchIsBoundedAndDoesNotAuthorizeOtherInputs()
    {
        var manifest = DocumentationLiveUIManifest.Create();
        Assert.Empty(new LiveUIManifestValidator().Validate(manifest));
        var route = manifest.Routes.Single(item => item.Path == "/docs");
        var search = route.Targets.Single(item => item.Id == "documentation-search");
        Assert.Contains(LiveUICapability.SetValue, search.Capabilities);
        var action = new LiveUIAction("action", LiveUISchema.CurrentVersion, "session", manifest.ContentHash,
            route.Id, search.Id, LiveUIActionType.SetValue, new(Value: "privacy"), TimeSpan.FromSeconds(1));
        var authorizer = new DocumentationLiveUIActionAuthorizer();
        Assert.True((await authorizer.AuthorizeAsync(new(manifest, route, search, action))).Allowed);
        Assert.False((await authorizer.AuthorizeAsync(new(manifest, route, search,
            action with { Parameters = new(Value: new string('a', 201)) }))).Allowed);
        Assert.False((await authorizer.AuthorizeAsync(new(manifest, route, search with { Id = "other-input" }, action))).Allowed);
        Assert.False((await authorizer.AuthorizeAsync(new(manifest, route with { Path = "/" }, search, action))).Allowed);
    }
}
