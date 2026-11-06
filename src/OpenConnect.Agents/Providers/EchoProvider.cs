namespace OpenConnect.Agents.Providers;

/// <summary>A deterministic chat-only provider for tests and local examples.</summary>
public sealed class EchoProvider : IProviderAdapter
{
    /// <inheritdoc />
    public ProviderDescriptor Descriptor { get; } = new("echo", true, false, false, false);

    /// <inheritdoc />
    public ValueTask<ProviderResponse> SendAsync(ProviderRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new ProviderResponse(request.Prompt));
    }
}
