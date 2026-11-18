namespace OpenConnect.Agents.Execution;

/// <summary>Identifies the authoritative owner of conversation/model history for a provider.</summary>
public enum ModelHistoryOwner
{
    /// <summary>The host application and MAF session persistence own history locally.</summary>
    NativeMafSession = 1,

    /// <summary>The upstream model provider owns history remotely (e.g. OpenAI Assistants threads).</summary>
    ServiceManaged = 2
}

/// <summary>Declares the authoritative history ownership policy for a model provider.</summary>
/// <param name="ProviderId">Stable provider identifier.</param>
/// <param name="AuthoritativeOwner">Designated single authoritative owner.</param>
public sealed record ModelHistoryProviderPolicy(string ProviderId, ModelHistoryOwner AuthoritativeOwner)
{
    /// <summary>Gets the provider identifier, ensuring it is non-empty.</summary>
    public string ProviderId { get; init; } = string.IsNullOrWhiteSpace(ProviderId)
        ? throw new ArgumentException("Provider ID is required.", nameof(ProviderId))
        : ProviderId;
}

/// <summary>Validates provider history ownership policies, enforcing that exactly one owner is declared.</summary>
public static class ModelHistoryOwnershipValidator
{
    /// <summary>Validates that the policy designates exactly one valid authoritative owner.</summary>
    /// <param name="policy">The provider policy to validate.</param>
    /// <exception cref="ArgumentNullException">Thrown when policy is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when ownership is ambiguous, undefined, or contradicts invariants.</exception>
    public static void Validate(ModelHistoryProviderPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (string.IsNullOrWhiteSpace(policy.ProviderId))
            throw new InvalidOperationException("Provider history policy must specify a valid ProviderId.");

        if (!Enum.IsDefined(policy.AuthoritativeOwner))
            throw new InvalidOperationException(
                $"Provider '{policy.ProviderId}' has an invalid history ownership setting: {policy.AuthoritativeOwner}. " +
                "A provider must designate exactly one authoritative owner: NativeMafSession or ServiceManaged, never both or neither.");
    }

    /// <summary>Verifies that a provider uses native MAF session history.</summary>
    public static bool IsNativeMafSession(ModelHistoryProviderPolicy policy)
    {
        Validate(policy);
        return policy.AuthoritativeOwner == ModelHistoryOwner.NativeMafSession;
    }

    /// <summary>Verifies that a provider uses service-managed history.</summary>
    public static bool IsServiceManaged(ModelHistoryProviderPolicy policy)
    {
        Validate(policy);
        return policy.AuthoritativeOwner == ModelHistoryOwner.ServiceManaged;
    }
}
