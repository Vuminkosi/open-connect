using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Privacy.Gates;
using OpenConnect.Privacy.Policy;
using Xunit;

namespace OpenConnect.Privacy.Tests;

public sealed class ModalityEgressGateTests
{
    private static readonly EgressContext ExternalContext = new(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.ExternalModel);
    private static readonly EgressContext LocalContext = new(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.Worker);

    [Fact]
    public void RawAudioModalityIsProhibitedFromRemoteEgressUnderStrictLocal()
    {
        var firewall = new PrivacyFirewall(Options.Create(new PrivacyOptions { Enabled = true, Mode = FirewallMode.StrictLocal }), new PolicyEngine());
        var gate = new ModalityEgressGate(firewall);

        var request = new ModalityEgressRequest(ContentModality.Audio, ExternalContext);
        var decision = gate.EvaluateModality(request, FirewallMode.StrictLocal);

        Assert.False(decision.Allowed);
        Assert.Equal("raw_audio_remote_egress_forbidden", decision.Reason);
    }

    [Fact]
    public void UnredactedImageOrDocumentIsProhibitedFromRemoteEgress()
    {
        var firewall = new PrivacyFirewall(Options.Create(new PrivacyOptions { Enabled = true, Mode = FirewallMode.StrictLocal }), new PolicyEngine());
        var gate = new ModalityEgressGate(firewall);

        var request = new ModalityEgressRequest(ContentModality.Image, ExternalContext, HasVerifiedLocalRedaction: false);
        var decision = gate.EvaluateModality(request, FirewallMode.StrictLocal);

        Assert.False(decision.Allowed);
        Assert.Equal("unredacted_media_remote_egress_forbidden", decision.Reason);
    }

    [Fact]
    public void VerifiedRedactedImageAllowedForRemoteEgress()
    {
        var firewall = new PrivacyFirewall(Options.Create(new PrivacyOptions { Enabled = true, Mode = FirewallMode.StrictLocal }), new PolicyEngine());
        var gate = new ModalityEgressGate(firewall);

        var request = new ModalityEgressRequest(ContentModality.Image, ExternalContext, HasVerifiedLocalRedaction: true);
        var decision = gate.EvaluateModality(request, FirewallMode.StrictLocal);

        Assert.True(decision.Allowed);
        Assert.Equal("modality_egress_allowed", decision.Reason);
    }

    [Fact]
    public void DisabledPrivacyAllowsModalityEgressExplicitly()
    {
        var firewall = new PrivacyFirewall(Options.Create(new PrivacyOptions { Enabled = false, Mode = FirewallMode.Disabled }), new PolicyEngine());
        var gate = new ModalityEgressGate(firewall);

        var request = new ModalityEgressRequest(ContentModality.Audio, ExternalContext);
        var decision = gate.EvaluateModality(request, FirewallMode.Disabled);

        Assert.True(decision.Allowed);
        Assert.Equal("modality_egress_allowed_privacy_disabled", decision.Reason);
    }
}
