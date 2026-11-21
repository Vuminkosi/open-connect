using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Client;
using OpenConnect.Docs.Services;
using OpenConnect.LiveUI;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class DocumentationAssistantTests
{
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
}
