namespace OpenConnect.Agents.Providers;

/// <summary>Declares the operations a provider can perform. Capabilities are never inferred or substituted.</summary>
public sealed record ProviderDescriptor(
    string ProviderId,
    bool SupportsChat,
    bool SupportsStreaming,
    bool SupportsTools,
    bool SupportsStructuredOutput)
{
    /// <summary>Gets the stable provider identifier.</summary>
    public string ProviderId { get; init; } = string.IsNullOrWhiteSpace(ProviderId)
        ? throw new ArgumentException("Provider id is required.", nameof(ProviderId))
        : ProviderId;
}
