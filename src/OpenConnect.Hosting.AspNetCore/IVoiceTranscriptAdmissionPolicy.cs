namespace OpenConnect.Hosting.AspNetCore;

/// <summary>Optional host-owned timing and text customization for authoritative voice transcripts.</summary>
/// <remarks>
/// Register in dependency injection to opt in. Without a policy, the relay waits for its
/// existing stream-end fallback and preserves the accumulated transcript. Provider trimming
/// and segment selection still apply. This policy does not authorize, reject, or filter tasks.
/// </remarks>
public interface IVoiceTranscriptAdmissionPolicy
{
    /// <summary>Returns whether an authoritative accumulated transcript may be admitted before the stream ends.</summary>
    /// <remarks>False only delays admission; the best-final stream-end fallback remains active.</remarks>
    bool ShouldAdmitEarly(string authoritativeTranscript);

    /// <summary>Returns the non-null task text for an admitted authoritative accumulated transcript.</summary>
    /// <remarks>Called for both early and fallback admission; it must not be used as an authorization decision.</remarks>
    string NormalizeTranscript(string transcript);
}
