using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Docs.Components.Pages;
using OpenConnect.Docs.Services;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class DocumentationPageNavigationTests : BunitContext
{
    [Fact]
    public void SectionLinksKeepTheDocumentRouteWithARootBaseUrl()
    {
        var store = new DocumentationContentStore();
        store.Load();
        Services.AddSingleton(store);
        var page = Render<DocPage>(parameters => parameters
            .Add(p => p.Version, "v1")
            .Add(p => p.Slug, "dotnet-client"));

        var links = page.FindAll(".doc-toc a");
        Assert.NotEmpty(links);
        foreach (var link in links)
        {
            var href = link.GetAttribute("href") ?? throw new InvalidOperationException("Section link has no href.");
            Assert.StartsWith("/docs/v1/dotnet-client#", href);
            var fragment = Uri.UnescapeDataString(href[href.IndexOf('#')..]);
            Assert.NotNull(page.Find(fragment));
        }
    }
}
