namespace OpenConnect.Agents.Providers;

/// <summary>Represents a provider request using explicitly requested capabilities.</summary>
public sealed record ProviderRequest(
    string Prompt,
    bool UseStreaming = false,
    bool UseTools = false,
    bool UseStructuredOutput = false);

/// <summary>Provider response content.</summary>
public sealed record ProviderResponse(string Content);

/// <summary>Implemented by model-provider integrations.</summary>
public interface IProviderAdapter
{
    /// <summary>Gets the provider's declared capabilities.</summary>
    ProviderDescriptor Descriptor { get; }
    /// <summary>Sends a request to this provider.</summary>
    ValueTask<ProviderResponse> SendAsync(ProviderRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Dispatches requests only to the explicitly selected compatible provider.</summary>
public sealed class ProviderAdapter
{
    private readonly IReadOnlyDictionary<string, IProviderAdapter> _providers;

    /// <summary>Creates a dispatcher for a fixed set of providers.</summary>
    public ProviderAdapter(IEnumerable<IProviderAdapter> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        var registered = new Dictionary<string, IProviderAdapter>(StringComparer.Ordinal);
        foreach (var provider in providers)
        {
            ArgumentNullException.ThrowIfNull(provider);
            if (!registered.TryAdd(provider.Descriptor.ProviderId, provider))
                throw new ArgumentException($"Provider '{provider.Descriptor.ProviderId}' is registered more than once.", nameof(providers));
        }
        _providers = registered;
    }

    /// <summary>Gets the declared capabilities of registered providers.</summary>
    public IReadOnlyCollection<ProviderDescriptor> Providers => _providers.Values.Select(provider => provider.Descriptor).ToArray();

    /// <summary>Sends a request to the explicitly named provider, failing when it lacks a requested capability.</summary>
    public ValueTask<ProviderResponse> SendAsync(string providerId, ProviderRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_providers.TryGetValue(providerId, out var provider))
            throw new KeyNotFoundException($"Provider '{providerId}' is not registered.");

        var capabilities = provider.Descriptor;
        if (request.UseStreaming && !capabilities.SupportsStreaming ||
            request.UseTools && !capabilities.SupportsTools ||
            request.UseStructuredOutput && !capabilities.SupportsStructuredOutput ||
            !request.UseStreaming && !capabilities.SupportsChat)
            throw new NotSupportedException($"Provider '{providerId}' does not support the requested operation.");

        return provider.SendAsync(request, cancellationToken);
    }
}
