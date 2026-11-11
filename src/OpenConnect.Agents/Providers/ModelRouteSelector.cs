using OpenConnect.Abstractions;

namespace OpenConnect.Agents.Providers;

/// <summary>
/// Selects and validates model providers against explicit agent model requirements.
/// Enforces fail-closed exact match, capability matching, locality constraints, and declared-fallback-only routing.
/// </summary>
public static class ModelRouteSelector
{
    /// <summary>
    /// Selects an appropriate provider matching the requirement, or falls back only to the explicitly declared fallback model.
    /// </summary>
    /// <param name="requirement">The model requirements to satisfy.</param>
    /// <param name="availableProviders">The available provider descriptors.</param>
    /// <returns>The matching provider descriptor.</returns>
    /// <exception cref="ArgumentNullException">Requirement or availableProviders is null.</exception>
    /// <exception cref="InvalidOperationException">No provider satisfies the requirements, or fail-closed constraints reject the match.</exception>
    public static ProviderDescriptor SelectProvider(
        ModelRequirement requirement,
        IEnumerable<ProviderDescriptor> availableProviders)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        ArgumentNullException.ThrowIfNull(availableProviders);

        var providers = availableProviders.ToList();

        // 1. If ModelId is specified, try exact match first
        if (!string.IsNullOrWhiteSpace(requirement.ModelId))
        {
            var primary = providers.FirstOrDefault(p => string.Equals(p.ProviderId, requirement.ModelId, StringComparison.Ordinal));
            if (primary != null)
            {
                var validation = ValidateProvider(primary, requirement);
                if (validation.IsValid)
                {
                    return primary;
                }

                // Primary provider matched by id but failed capability or locality requirements.
                // If a declared fallback exists, attempt fallback; otherwise fail closed.
                if (string.IsNullOrWhiteSpace(requirement.FallbackModelId))
                {
                    throw new InvalidOperationException(
                        $"Provider '{primary.ProviderId}' does not meet required model capabilities: {validation.FailureReason}");
                }
            }
            else
            {
                // Primary provider not found. If no declared fallback, fail closed.
                if (string.IsNullOrWhiteSpace(requirement.FallbackModelId))
                {
                    throw new InvalidOperationException(
                        $"Required model provider '{requirement.ModelId}' is not registered and no fallback is declared.");
                }
            }
        }

        // 2. Fallback routing: ONLY declared fallback is allowed (no arbitrary substitution).
        if (!string.IsNullOrWhiteSpace(requirement.FallbackModelId))
        {
            var fallback = providers.FirstOrDefault(p => string.Equals(p.ProviderId, requirement.FallbackModelId, StringComparison.Ordinal));
            if (fallback == null)
            {
                throw new InvalidOperationException(
                    $"Declared fallback provider '{requirement.FallbackModelId}' is not registered.");
            }

            var fallbackValidation = ValidateProvider(fallback, requirement);
            if (!fallbackValidation.IsValid)
            {
                throw new InvalidOperationException(
                    $"Declared fallback provider '{fallback.ProviderId}' does not meet requirements: {fallbackValidation.FailureReason}");
            }

            return fallback;
        }

        // 3. If no specific ModelId was requested, find the first provider matching all requirements
        var match = providers.FirstOrDefault(p => ValidateProvider(p, requirement).IsValid);
        if (match != null)
        {
            return match;
        }

        throw new InvalidOperationException("No registered provider satisfies the model requirements.");
    }

    private static (bool IsValid, string? FailureReason) ValidateProvider(
        ProviderDescriptor provider,
        ModelRequirement requirement)
    {
        if (requirement.RequireTools && !provider.SupportsTools)
            return (false, "Tools capability required but unsupported");
        if (requirement.RequireStreaming && !provider.SupportsStreaming)
            return (false, "Streaming capability required but unsupported");
        if (requirement.RequireStructuredOutput && !provider.SupportsStructuredOutput)
            return (false, "Structured output capability required but unsupported");
        if (requirement.RequireVision && !provider.SupportsVision)
            return (false, "Vision capability required but unsupported");

        if (requirement.Location.HasValue)
        {
            var reqLoc = requirement.Location.Value;
            if (reqLoc == ModelExecutionLocation.LocalOnly && provider.Locality != ModelExecutionLocation.LocalOnly)
                return (false, $"Locality constraint LocalOnly violated by provider locality {provider.Locality}");
            if (reqLoc == ModelExecutionLocation.SelfHosted && provider.Locality == ModelExecutionLocation.RemoteAllowed)
                return (false, $"Locality constraint SelfHosted violated by remote provider locality {provider.Locality}");
        }

        return (true, null);
    }
}
