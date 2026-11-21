using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Docs.Components.Pages;
using OpenConnect.Docs.Services;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class ApiReferenceComponentTests : BunitContext
{
    [Fact]
    public void RestAndWorkerContractTargetIsRendered()
    {
        Services.AddSingleton<GeneratedApiStore>();
        var component = Render<ApiIndex>();
        Assert.Equal("REST and worker contracts", component.Find("#api-rest-worker-contracts").TextContent);
    }

    [Fact]
    public void ApiReferenceOffersClientGuidesAndCommonOperationsWithoutGeneratedMetadata()
    {
        var missingDirectory = Path.Combine(Path.GetTempPath(), $"openconnect-missing-api-{Guid.NewGuid():N}");
        Services.AddSingleton(new GeneratedApiStore(missingDirectory));
        var component = Render<ApiIndex>();

        Assert.NotNull(component.Find("a[href='/docs/v1/dotnet-client']"));
        Assert.NotNull(component.Find("a[href='/docs/v1/typescript-client']"));
        Assert.NotNull(component.Find("a[href='/docs/v1/rest-api']"));
        Assert.Contains("CreateTaskAsync", component.Find(".api-operation-table").TextContent);
        Assert.Contains("createTask", component.Find(".api-operation-table").TextContent);
        Assert.Contains("POST /tasks", component.Find(".api-operation-table").TextContent);
        Assert.Contains("Generated metadata is not included", component.Markup);
    }

    [Fact]
    public void LiveUiHeadingMatchesTheRenderedApiPage()
    {
        Services.AddSingleton<GeneratedApiStore>();
        var component = Render<ApiIndex>();
        var route = DocumentationLiveUIManifest.Create().Routes.Single(item => item.Path == "/api");
        var target = route.Targets.Single(item => item.Id == "documentation-api-heading");

        Assert.Equal("main h1", target.Selector);
        Assert.Equal(component.Find("h1").TextContent, target.Label);
    }

    [Fact]
    public void GeneratedDocumentsCanBeSearchedAndPaged()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"openconnect-api-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            for (var index = 0; index < 21; index++)
                File.WriteAllText(Path.Combine(directory, $"Type{index:D2}.yml"), $"document {index}");
            Services.AddSingleton(new GeneratedApiStore(directory));
            var component = Render<ApiIndex>();
            Assert.Equal(20, component.FindAll(".api-list details").Count);
            Assert.Contains("Page 1 of 2", component.Markup);
            component.FindAll("button").Single(button => button.TextContent == "Next").Click();
            Assert.Single(component.FindAll(".api-list details"));
            component.Find("#api-search").Input("type05");
            Assert.Single(component.FindAll(".api-list details"));
            Assert.Contains("Type05", component.Markup);
            Assert.Contains("Page 1 of 1", component.Markup);
            component.Find("#api-search").Input("missing");
            Assert.Contains("No API documents match your search.", component.Markup);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
