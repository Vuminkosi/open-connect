using OpenConnect.Agents.Templates;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class InstructionLoaderAdapterTests
{
    [Theory]
    [InlineData("../outside.md")]
    [InlineData("/outside.md")]
    public async Task LoadAsync_RejectsUnsafePathWithoutCallingLoader(string path)
    {
        var loaderCalls = 0;
        var adapter = new InstructionLoaderAdapter((_, _) =>
        {
            loaderCalls++;
            return Task.FromResult(("content", "revision"));
        });

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => adapter.LoadAsync(path));

        Assert.Equal(0, loaderCalls);
    }

    [Fact]
    public async Task LoadAsync_PassesSafeRelativePathToLoader()
    {
        var loaderCalls = 0;
        var adapter = new InstructionLoaderAdapter((path, _) =>
        {
            loaderCalls++;
            return Task.FromResult((path, "revision"));
        });

        var loaded = await adapter.LoadAsync("instructions/guide.md");

        Assert.Equal(("instructions/guide.md", "revision"), loaded);
        Assert.Equal(1, loaderCalls);
    }
}
