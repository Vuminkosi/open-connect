using OpenConnect.Abstractions;

namespace OpenConnect.Agents.Providers;

/// <summary>Declares the operations a provider can perform. Capabilities are never inferred or substituted.</summary>
public sealed record ProviderDescriptor(
    string ProviderId,
    bool SupportsChat,
    bool SupportsStreaming,
    bool SupportsTools,
    bool SupportsStructuredOutput,
    bool SupportsVision = false,
    ModelExecutionLocation Locality = ModelExecutionLocation.LocalOnly)
{
    /// <summary>Creates a descriptor for chat, streaming, tools, and structured output capabilities.</summary>
    public ProviderDescriptor(
        string providerId,
        bool supportsChat,
        bool supportsStreaming,
        bool supportsTools,
        bool supportsStructuredOutput)
        : this(providerId, supportsChat, supportsStreaming, supportsTools, supportsStructuredOutput, false, ModelExecutionLocation.LocalOnly)
    {
    }

    /// <summary>Gets the stable provider identifier.</summary>
    public string ProviderId { get; init; } = string.IsNullOrWhiteSpace(ProviderId)
        ? throw new ArgumentException("Provider id is required.", nameof(ProviderId))
        : ProviderId;
}
