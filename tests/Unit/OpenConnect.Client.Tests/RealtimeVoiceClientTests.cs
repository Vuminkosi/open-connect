using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Abstractions.Speech;
using OpenConnect.Client;
using Xunit;

namespace OpenConnect.Client.Tests;

public sealed class RealtimeVoiceClientTests
{
    [Fact]
    public void VoiceClientOptions_DuplexFlagsDefaultToFalseWhilePreservingExistingDefaults()
    {
        var options = new VoiceClientOptions();

        Assert.True(options.EnableLiveTranscription);
        Assert.True(options.EnableVoiceRecording);
        Assert.True(options.EnablePlayback);
        Assert.True(options.EnableBargeIn);
        Assert.False(options.EnableContinuousConversation);
        Assert.False(options.EnableStreamingPlayback);
        Assert.False(options.EnableSemanticTurnDetection);
    }

    [Fact]
    public void AddOpenConnectClient_RegistersIRealtimeVoiceClient()
    {
        var services = new ServiceCollection();
        services.AddOpenConnectClient(new Uri("https://gateway.example.test/openconnect/v1"));

        using var provider = services.BuildServiceProvider();
        var voiceClient = provider.GetRequiredService<IRealtimeVoiceClient>();

        Assert.NotNull(voiceClient);
        Assert.IsType<OpenConnectClient>(voiceClient);
    }

    [Fact]
    public void AddOpenConnectClient_OptionsOverload_RegistersIRealtimeVoiceClient()
    {
        var services = new ServiceCollection();
        services.AddOpenConnectClient(options =>
        {
            options.ApiBaseAddress = new Uri("https://gateway.example.test/openconnect/v1");
            options.WebSocketAddress = new Uri("wss://gateway.example.test/openconnect/v1");
        });

        using var provider = services.BuildServiceProvider();
        var voiceClient = provider.GetRequiredService<IRealtimeVoiceClient>();

        Assert.NotNull(voiceClient);
        Assert.IsType<OpenConnectClient>(voiceClient);
    }
}
