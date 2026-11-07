namespace OpenConnect.Agents.Providers;

/// <summary>A deterministic provider advertising chat and streaming, for capability tests.</summary>
public sealed class StreamingTestProvider : IProviderAdapter
{
    /// <inheritdoc />
    public ProviderDescriptor Descriptor { get; } = new("streaming-test", true, true, false, false);

    /// <inheritdoc />
    public ValueTask<ProviderResponse> SendAsync(ProviderRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new ProviderResponse(request.Prompt));
    }
}
