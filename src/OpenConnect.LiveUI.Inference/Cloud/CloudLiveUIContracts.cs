using OpenConnect.Privacy;

namespace OpenConnect.LiveUI.Inference;

/// <summary>Authorizes a LiveUI request against the host's current authenticated session and route.</summary>
/// <remarks>The host must verify that the requested capabilities belong to the current authorized view.</remarks>
public interface ICloudLiveUIHostAuthorizer
{
    /// <summary>Returns the authenticated external-model egress context, or null when access is denied.</summary>
    ValueTask<EgressContext?> AuthorizeAsync(LiveUIInferenceRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Transports a protected Cloud LiveUI request from the application server.</summary>
public interface ICloudLiveUITransport
{
    /// <summary>Checks whether the Cloud gateway advertises LiveUI readiness.</summary>
    Task<bool> IsReadyAsync(CancellationToken cancellationToken = default);

    /// <summary>Submits an already screened Cloud LiveUI request.</summary>
    Task<CloudLiveUITransportResult> SendAsync(ReadOnlyMemory<byte> request, CancellationToken cancellationToken = default);
}

/// <summary>Contains the HTTP disposition and bounded response bytes from the Cloud gateway.</summary>
public sealed record CloudLiveUITransportResult(int StatusCode, byte[] Body);
