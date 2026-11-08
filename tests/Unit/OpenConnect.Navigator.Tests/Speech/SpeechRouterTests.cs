using OpenConnect.Navigator;
using Xunit;

namespace OpenConnect.Navigator.Tests.Speech;

public sealed class SpeechRouterTests
{
    [Fact]
    public void Disabled_or_missing_or_unhealthy_provider_resolves_text_only()
    {
        var options = new NavigatorOptions().Speech;
        var router = new SpeechRouter(options);
        Assert.False(router.ResolveProvider([]).IsAvailable);
        options.Enabled = true;
        Assert.False(router.ResolveProvider([]).IsAvailable);
        Assert.False(router.ResolveProvider([new("Kokoro", NavigatorSpeechProvider.Kokoro, true, false)]).IsAvailable);
        Assert.Equal("Kokoro", router.ResolveProvider([new("Kokoro", NavigatorSpeechProvider.Kokoro, true, true)]).ProviderId);
    }

    [Fact]
    public void Interrupt_invalidates_events_from_prior_epoch()
    {
        var options = new NavigatorOptions().Speech;
        options.Enabled = true;
        var router = new SpeechRouter(options);
        var request = router.CreateRequest("req", "vault://text/1");
        Assert.True(router.IsCurrent(new(SpeechSynthesisEventKind.Started, request.RequestId, request.SpeechEpoch)));
        router.Interrupt();
        Assert.False(router.IsCurrent(new(SpeechSynthesisEventKind.AudioChunk, request.RequestId, request.SpeechEpoch, 0)));
    }

    [Fact]
    public void Disabled_configuration_rejects_request_and_request_is_reference_based()
    {
        var router = new SpeechRouter(new NavigatorOptions().Speech);
        Assert.Throws<InvalidOperationException>(() => router.CreateRequest("r", "ref"));
    }
}
