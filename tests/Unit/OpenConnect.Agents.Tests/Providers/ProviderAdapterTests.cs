using OpenConnect.Agents.Providers;
using Xunit;

namespace OpenConnect.Agents.Tests.Providers;

public sealed class ProviderAdapterTests
{
    [Fact]
    public async Task Sends_to_explicit_provider_without_changing_provider()
    {
        var adapter = new ProviderAdapter([new EchoProvider(), new StreamingTestProvider()]);

        var result = await adapter.SendAsync("echo", new ProviderRequest("hello"));

        Assert.Equal("hello", result.Content);
        Assert.Equal(new ProviderDescriptor("echo", true, false, false, false), adapter.Providers.Single(p => p.ProviderId == "echo"));
    }

    [Fact]
    public async Task Unsupported_tool_request_fails_without_fallback()
    {
        var adapter = new ProviderAdapter([new EchoProvider(), new StreamingTestProvider()]);

        await Assert.ThrowsAsync<NotSupportedException>(() => adapter.SendAsync("echo", new ProviderRequest("hello", UseTools: true)).AsTask());
    }

    [Fact]
    public async Task Streaming_provider_accepts_streaming_and_echo_does_not()
    {
        var adapter = new ProviderAdapter([new EchoProvider(), new StreamingTestProvider()]);

        Assert.Equal("hello", (await adapter.SendAsync("streaming-test", new ProviderRequest("hello", UseStreaming: true))).Content);
        await Assert.ThrowsAsync<NotSupportedException>(() => adapter.SendAsync("echo", new ProviderRequest("hello", UseStreaming: true)).AsTask());
    }
}
