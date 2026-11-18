using Bunit;
using OpenConnect.Docs.Components;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class PortalComponentTests : BunitContext
{
    [Fact]
    public void MarkdownRenderer_EncodesRawHtmlAndRendersHeadings()
    {
        var cut = Render<MarkdownRenderer>(p => p.Add(x => x.Markdown, "# Hello\n\n<script>alert(1)</script>\n\n[Safe](/docs/v1/getting-started)"));

        Assert.Contains("<h2>Hello</h2>", cut.Markup);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", cut.Markup);
        Assert.Contains("href=\"/docs/v1/getting-started\"", cut.Markup);
        Assert.DoesNotContain("<script>", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MarkdownRenderer_EncodesUnsafeCodeBlocks()
    {
        var cut = Render<MarkdownRenderer>(p => p.Add(x => x.Markdown, "```csharp\nvar value = \"<tag>\";\n```"));

        Assert.Contains("&lt;tag&gt;", cut.Markup);
        Assert.DoesNotContain("<tag>", cut.Markup);
    }
}

public sealed class TodoStoreTests
{
    [Fact]
    public async Task InMemoryTodoStore_supports_create_update_complete_and_delete()
    {
        var store = new OpenConnect.Samples.Blazor.Models.InMemoryTodoStore();
        var created = await store.AddAsync("Portal regression task");

        Assert.NotNull(await store.GetByIdAsync(created.Id));
        Assert.True(await store.UpdateStatusAsync(created.Id, true));
        Assert.True((await store.GetByIdAsync(created.Id))!.IsCompleted);
        Assert.True(await store.DeleteAsync(created.Id));
        Assert.Null(await store.GetByIdAsync(created.Id));
    }

    [Fact]
    public async Task InMemoryTodoStore_seeds_reference_tasks_without_external_dependencies()
    {
        var store = new OpenConnect.Samples.Blazor.Models.InMemoryTodoStore();
        var items = await store.GetAllAsync();

        Assert.Equal(3, items.Count);
        Assert.Contains(items, item => item.Title.Contains("privacy firewall", StringComparison.OrdinalIgnoreCase));
    }
}
