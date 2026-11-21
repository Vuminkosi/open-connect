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
