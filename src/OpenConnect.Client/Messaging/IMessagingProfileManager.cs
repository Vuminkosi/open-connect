using System.Collections.Concurrent;
using OpenConnect.Abstractions.Messaging;

namespace OpenConnect.Client.Messaging;

/// <summary>
/// Storage seam for per-profile non-secret metadata and platform-protected secret credentials.
/// Implementations MUST namespace all keys by <c>profileId</c> and fail closed on secure storage errors.
/// </summary>
public interface IMessagingProfileStorage
{
    ValueTask<IReadOnlyList<MessagingProfileRecord>> ListProfilesAsync(CancellationToken cancellationToken = default);

    ValueTask<MessagingProfileRecord?> GetProfileAsync(string profileId, CancellationToken cancellationToken = default);

    ValueTask SaveProfileAsync(MessagingProfileRecord profile, CancellationToken cancellationToken = default);

    ValueTask DeleteProfileAsync(string profileId, CancellationToken cancellationToken = default);

    ValueTask<MessagingProfileCredentials?> GetCredentialsAsync(string profileId, CancellationToken cancellationToken = default);

    ValueTask SaveCredentialsAsync(string profileId, MessagingProfileCredentials credentials, CancellationToken cancellationToken = default);

    ValueTask DeleteCredentialsAsync(string profileId, CancellationToken cancellationToken = default);

    ValueTask<string?> GetActiveProfileIdAsync(CancellationToken cancellationToken = default);

    ValueTask SetActiveProfileIdAsync(string? profileId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Thread-safe in-memory profile storage for unit tests and ephemeral browser/desktop sessions.
/// Mobile hosts MUST register a platform-backed <see cref="IMessagingProfileStorage"/> using SecureStorage/Keystore.
/// </summary>
public sealed class InMemoryMessagingProfileStorage : IMessagingProfileStorage
{
    private readonly ConcurrentDictionary<string, MessagingProfileRecord> _profiles = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, MessagingProfileCredentials> _credentials = new(StringComparer.Ordinal);
    private string? _activeProfileId;

    public ValueTask<IReadOnlyList<MessagingProfileRecord>> ListProfilesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<MessagingProfileRecord> list = _profiles.Values
            .OrderBy(p => p.CompanyDisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Identity.ProfileId, StringComparer.Ordinal)
            .ToArray();
        return ValueTask.FromResult(list);
    }

    public ValueTask<MessagingProfileRecord?> GetProfileAsync(string profileId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _profiles.TryGetValue(profileId, out var profile);
        return ValueTask.FromResult(profile);
    }

    public ValueTask SaveProfileAsync(MessagingProfileRecord profile, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(profile);
        _profiles[profile.Identity.ProfileId] = profile;
        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteProfileAsync(string profileId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _profiles.TryRemove(profileId, out _);
        _credentials.TryRemove(profileId, out _);
        if (string.Equals(_activeProfileId, profileId, StringComparison.Ordinal))
        {
            _activeProfileId = null;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<MessagingProfileCredentials?> GetCredentialsAsync(string profileId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _credentials.TryGetValue(profileId, out var creds);
        return ValueTask.FromResult(creds);
    }

    public ValueTask SaveCredentialsAsync(string profileId, MessagingProfileCredentials credentials, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(credentials);
        _credentials[profileId] = credentials;
        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteCredentialsAsync(string profileId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _credentials.TryRemove(profileId, out _);
        return ValueTask.CompletedTask;
    }

    public ValueTask<string?> GetActiveProfileIdAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_activeProfileId);
    }

    public ValueTask SetActiveProfileIdAsync(string? profileId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _activeProfileId = profileId;
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Manages multi-company messaging profiles with strict isolation:
/// at most one active profile at a time, serialized switching with active-call confirmation,
/// monotonic epoch fencing to reject late callbacks, single-flight refresh token rotation,
/// and per-profile scoped <see cref="HttpClient"/> creation without mutating global singletons.
/// </summary>
public interface IMessagingProfileManager : IAsyncDisposable
{
    /// <summary>Current monotonic profile epoch. Increments on every switch, logout, or revocation.</summary>
    long CurrentEpoch { get; }

    /// <summary>Currently active profile, or <see langword="null"/> when zero profiles are active.</summary>
    MessagingProfileRecord? ActiveProfile { get; }

    /// <summary>Whether the active profile currently has an in-progress voice/media call.</summary>
    bool HasActiveCall { get; }

    /// <summary>Raised when the active profile or epoch changes.</summary>
    event Action<MessagingProfileRecord?, long>? ActiveProfileChanged;

    /// <summary>Checks whether a callback belongs to the current active profile and epoch.</summary>
    bool IsCurrentEpoch(long epoch, string profileId);

    /// <summary>Updates the active-call indicator so profile switching requires explicit user confirmation.</summary>
    void SetActiveCallState(bool hasActiveCall, Func<CancellationToken, ValueTask>? endActiveCallCallback = null);

    /// <summary>Loads stored profiles and restores the last active valid profile, if any.</summary>
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>Lists all stored profiles.</summary>
    ValueTask<IReadOnlyList<MessagingProfileRecord>> ListProfilesAsync(CancellationToken cancellationToken = default);

    /// <summary>Enrolls/saves a profile with its credentials and optionally activates it.</summary>
    ValueTask<MessagingProfileRecord> AddOrUpdateProfileAsync(
        MessagingProfileRecord profile,
        MessagingProfileCredentials credentials,
        bool activate = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Serialized active-profile switch. Fails closed with <see cref="MessagingProfileSwitchResult.ActiveCallConfirmationRequired"/>
    /// if a call is active and <paramref name="confirmEndActiveCall"/> is <see langword="false"/>.
    /// </summary>
    ValueTask<MessagingProfileSwitchResult> SwitchProfileAsync(
        string targetProfileId,
        bool confirmEndActiveCall = false,
        CancellationToken cancellationToken = default);

    /// <summary>Deactivates the current profile, cancels in-flight operations, and increments the epoch.</summary>
    ValueTask DeactivateCurrentProfileAsync(CancellationToken cancellationToken = default);

    /// <summary>Marks a profile revoked, wipes local credentials, and deactivates it if currently active.</summary>
    ValueTask MarkProfileRevokedAsync(string profileId, CancellationToken cancellationToken = default);

    /// <summary>Removes a profile and wipes its local credentials and metadata.</summary>
    ValueTask RemoveProfileAsync(string profileId, CancellationToken cancellationToken = default);

    /// <summary>Updates the durable event replay cursor for a profile if the epoch still matches.</summary>
    ValueTask UpdateCursorAsync(string profileId, long expectedEpoch, string cursor, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a valid access token for the specified profile, executing a single-flight refresh
    /// if expiring within <paramref name="refreshSkew"/> or when <paramref name="forceRefresh"/> is true.
    /// </summary>
    ValueTask<string> GetValidAccessTokenAsync(
        string profileId,
        Func<MessagingProfileIdentity, string, CancellationToken, ValueTask<MobileTokenBundle>> refreshCallback,
        bool forceRefresh = false,
        TimeSpan? refreshSkew = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates an isolated <see cref="HttpClient"/> scoped strictly to <paramref name="profile"/>
    /// with auto-redirect disabled so Authorization headers never leak across origins.
    /// </summary>
    HttpClient CreateScopedHttpClient(MessagingProfileIdentity profile);

    /// <summary>Returns a cancellation token that is cancelled immediately when the active profile switches.</summary>
    CancellationToken ActiveProfileCancellationToken { get; }
}
