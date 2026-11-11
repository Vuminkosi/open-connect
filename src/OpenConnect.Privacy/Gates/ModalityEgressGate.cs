using OpenConnect.Abstractions;

namespace OpenConnect.Privacy.Gates;

/// <summary>Identifies the modality of content being evaluated for egress.</summary>
public enum ContentModality
{
    /// <summary>Plain or structured text.</summary>
    Text,
    /// <summary>Raw or encoded audio waveform.</summary>
    Audio,
    /// <summary>Bitmap, PNG, JPEG, or other raster/vector image.</summary>
    Image,
    /// <summary>PDF, Word, or other multi-layer document.</summary>
    Document
}

/// <summary>Describes a modality-specific egress request.</summary>
/// <param name="Modality">The content modality.</param>
/// <param name="Context">Egress context.</param>
/// <param name="HasVerifiedLocalRedaction">Whether local pixel/audio redaction has been verified.</param>
public sealed record ModalityEgressRequest(ContentModality Modality, EgressContext Context, bool HasVerifiedLocalRedaction = false);

/// <summary>
/// Enforces modality trust boundaries. Under strict-local rules, raw audio or unredacted documents/images
/// are prohibited from remote model egress (transcript-only redaction does not protect remote raw audio).
/// </summary>
public sealed class ModalityEgressGate(IPrivacyFirewall firewall)
{
    private readonly IPrivacyFirewall _firewall = firewall ?? throw new ArgumentNullException(nameof(firewall));

    /// <summary>Evaluates whether the modality content may egress to the requested destination.</summary>
    public EgressDecision EvaluateModality(ModalityEgressRequest request, FirewallMode mode)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_firewall.Enabled || mode == FirewallMode.Disabled)
        {
            return new EgressDecision(true, null, Array.Empty<EntitySpan>(), SecurityPolicyAction.Allow, "modality_egress_allowed_privacy_disabled");
        }

        // Audio modality strict-mode boundary: raw audio must never reach external model providers
        if (request.Modality == ContentModality.Audio && request.Context.Destination == EgressDestination.ExternalModel)
        {
            return new EgressDecision(
                false,
                null,
                Array.Empty<EntitySpan>(),
                SecurityPolicyAction.Block,
                "raw_audio_remote_egress_forbidden");
        }

        // Image / document boundary: external egress requires verified local redaction
        if ((request.Modality == ContentModality.Image || request.Modality == ContentModality.Document) &&
            request.Context.Destination == EgressDestination.ExternalModel &&
            !request.HasVerifiedLocalRedaction)
        {
            return new EgressDecision(
                false,
                null,
                Array.Empty<EntitySpan>(),
                SecurityPolicyAction.Block,
                "unredacted_media_remote_egress_forbidden");
        }

        return new EgressDecision(true, null, Array.Empty<EntitySpan>(), SecurityPolicyAction.Allow, "modality_egress_allowed");
    }
}
