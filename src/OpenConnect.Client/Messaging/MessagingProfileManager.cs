using System.Collections.Concurrent;
using OpenConnect.Abstractions.Messaging;

namespace OpenConnect.Client.Messaging;

/// <summary>
/// Default implementation of <see cref="IMessagingProfileManager"/> enforcing strict multi-company
/// isolation, single active profile, serialized switching, epoch fencing, and single-flight token refresh.
/// </summary>
public sealed class MessagingProfileManager : IMessagingProfileManager
{
    private static readonly TimeSpan DefaultRefreshSkew = TimeSpan.FromSeconds(60);

    private readonly IMessagingProfileStorage _storage;
    private readonly Func<MessagingProfileIdentity, HttpMessageHandler>? _handlerFactory;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _switchLock = new(1, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _refreshLocks = new(StringComparer.Ordinal);

    private long _epoch;
    private MessagingProfileRecord? _activeProfile;
    private CancellationTokenSource _activeProfileCts = new();
    private bool _hasActiveCall;
    private Func<CancellationToken, ValueTask>? _endActiveCallCallback;
    private bool _disposed;

    public MessagingProfileManager(
        IMessagingProfileStorage storage,
        Func<MessagingProfileIdentity, HttpMessageHandler>? handlerFactory = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(storage);
        _storage = storage;
        _handlerFactory = handlerFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public long CurrentEpoch => Interlocked.Read(ref _epoch);

    public MessagingProfileRecord? ActiveProfile => Volatile.Read(ref _activeProfile);

    public bool HasActiveCall => Volatile.Read(ref _hasActiveCall);

    public CancellationToken ActiveProfileCancellationToken => _activeProfileCts.Token;

    public event Action<MessagingProfileRecord?, long>? ActiveProfileChanged;

    public bool IsCurrentEpoch(long epoch, string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return false;
        }

        var current = Volatile.Read(ref _activeProfile);
        return current is not null
            && Interlocked.Read(ref _epoch) == epoch
            && string.Equals(current.Identity.ProfileId, profileId, StringComparison.Ordinal);
    }

    public void SetActiveCallState(bool hasActiveCall, Func<CancellationToken, ValueTask>? endActiveCallCallback = null)
    {
        Volatile.Write(ref _hasActiveCall, hasActiveCall);
        _endActiveCallCallback = hasActiveCall ? endActiveCallCallback : null;
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _switchLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var activeId = await _storage.GetActiveProfileIdAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(activeId))
            {
                var profile = await _storage.GetProfileAsync(activeId, cancellationToken).ConfigureAwait(false);
                if (profile is not null && profile.State != MessagingProfileState.Revoked)
                {
                    var activated = profile with { State = MessagingProfileState.Active };
                    await _storage.SaveProfileAsync(activated, cancellationToken).ConfigureAwait(false);
                    Volatile.Write(ref _activeProfile, activated);
                    var nextEpoch = Interlocked.Increment(ref _epoch);
                    ActiveProfileChanged?.Invoke(activated, nextEpoch);
                    return;
                }
            }

            var profiles = await _storage.ListProfilesAsync(cancellationToken).ConfigureAwait(false);
            var firstValid = profiles.FirstOrDefault(p => p.State != MessagingProfileState.Revoked);
            if (firstValid is not null)
            {
                var activated = firstValid with { State = MessagingProfileState.Active };
                await _storage.SaveProfileAsync(activated, cancellationToken).ConfigureAwait(false);
                await _storage.SetActiveProfileIdAsync(activated.Identity.ProfileId, cancellationToken).ConfigureAwait(false);
                Volatile.Write(ref _activeProfile, activated);
                var nextEpoch = Interlocked.Increment(ref _epoch);
                ActiveProfileChanged?.Invoke(activated, nextEpoch);
            }
            else
            {
                Volatile.Write(ref _activeProfile, null);
                await _storage.SetActiveProfileIdAsync(null, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _switchLock.Release();
        }
    }

    public ValueTask<IReadOnlyList<MessagingProfileRecord>> ListProfilesAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _storage.ListProfilesAsync(cancellationToken);
    }

    public async ValueTask<MessagingProfileRecord> AddOrUpdateProfileAsync(
        MessagingProfileRecord profile,
        MessagingProfileCredentials credentials,
        bool activate = true,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(credentials);

        await _switchLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _storage.SaveCredentialsAsync(profile.Identity.ProfileId, credentials, cancellationToken).ConfigureAwait(false);

            var stored = profile with
            {
                State = activate ? MessagingProfileState.Active : MessagingProfileState.Inactive
            };
            await _storage.SaveProfileAsync(stored, cancellationToken).ConfigureAwait(false);

            if (activate)
            {
                var previous = Volatile.Read(ref _activeProfile);
                if (previous is not null
                    && !string.Equals(previous.Identity.ProfileId, stored.Identity.ProfileId, StringComparison.Ordinal)
                    && previous.State == MessagingProfileState.Active)
                {
                    await _storage.SaveProfileAsync(previous with { State = MessagingProfileState.Inactive }, cancellationToken).ConfigureAwait(false);
                }

                RotateCancellationSource();
                await _storage.SetActiveProfileIdAsync(stored.Identity.ProfileId, cancellationToken).ConfigureAwait(false);
                Volatile.Write(ref _activeProfile, stored);
                var nextEpoch = Interlocked.Increment(ref _epoch);
                ActiveProfileChanged?.Invoke(stored, nextEpoch);
            }

            return stored;
        }
        finally
        {
            _switchLock.Release();
        }
    }

    public async ValueTask<MessagingProfileSwitchResult> SwitchProfileAsync(
        string targetProfileId,
        bool confirmEndActiveCall = false,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetProfileId);

        await _switchLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = Volatile.Read(ref _activeProfile);
            if (current is not null && string.Equals(current.Identity.ProfileId, targetProfileId, StringComparison.Ordinal))
            {
                return new MessagingProfileSwitchResult(true, current.Identity.ProfileId, CurrentEpoch);
            }

            var target = await _storage.GetProfileAsync(targetProfileId, cancellationToken).ConfigureAwait(false);
            if (target is null)
            {
                return new MessagingProfileSwitchResult(
                    false,
                    current?.Identity.ProfileId,
                    CurrentEpoch,
                    ErrorCode: "profile_not_found",
                    ErrorMessage: "Requested company profile does not exist.");
            }

            if (target.State == MessagingProfileState.Revoked)
            {
                return new MessagingProfileSwitchResult(
                    false,
                    current?.Identity.ProfileId,
                    CurrentEpoch,
                    ErrorCode: "profile_revoked",
                    ErrorMessage: "This device profile was revoked. Re-pair with desktop to continue.");
            }

            if (Volatile.Read(ref _hasActiveCall))
            {
                if (!confirmEndActiveCall)
                {
                    return new MessagingProfileSwitchResult(
                        false,
                        current?.Identity.ProfileId,
                        CurrentEpoch,
                        ActiveCallConfirmationRequired: true,
                        ErrorCode: "active_call_in_progress",
                        ErrorMessage: "End the active call before switching company profiles.");
                }

                var endCall = _endActiveCallCallback;
                if (endCall is not null)
                {
                    await endCall(cancellationToken).ConfigureAwait(false);
                }

                Volatile.Write(ref _hasActiveCall, false);
                _endActiveCallCallback = null;
            }

            // Increment epoch and cancel in-flight requests/refreshes for the outgoing profile first.
            var nextEpoch = Interlocked.Increment(ref _epoch);
            RotateCancellationSource();

            if (current is not null && current.State == MessagingProfileState.Active)
            {
                await _storage.SaveProfileAsync(current with { State = MessagingProfileState.Inactive }, cancellationToken).ConfigureAwait(false);
            }

            var activated = target with { State = MessagingProfileState.Active };
            await _storage.SaveProfileAsync(activated, cancellationToken).ConfigureAwait(false);
            await _storage.SetActiveProfileIdAsync(activated.Identity.ProfileId, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _activeProfile, activated);

            ActiveProfileChanged?.Invoke(activated, nextEpoch);
            return new MessagingProfileSwitchResult(true, activated.Identity.ProfileId, nextEpoch);
        }
        finally
        {
            _switchLock.Release();
        }
    }

    public async ValueTask DeactivateCurrentProfileAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _switchLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var nextEpoch = Interlocked.Increment(ref _epoch);
            RotateCancellationSource();
            Volatile.Write(ref _hasActiveCall, false);
            _endActiveCallCallback = null;

            var current = Volatile.Read(ref _activeProfile);
            if (current is not null && current.State == MessagingProfileState.Active)
            {
                await _storage.SaveProfileAsync(current with { State = MessagingProfileState.Inactive }, cancellationToken).ConfigureAwait(false);
            }

            await _storage.SetActiveProfileIdAsync(null, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _activeProfile, null);
            ActiveProfileChanged?.Invoke(null, nextEpoch);
        }
        finally
        {
            _switchLock.Release();
        }
    }

    public async ValueTask MarkProfileRevokedAsync(string profileId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        await _switchLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _storage.DeleteCredentialsAsync(profileId, cancellationToken).ConfigureAwait(false);
            var existing = await _storage.GetProfileAsync(profileId, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                await _storage.SaveProfileAsync(existing with { State = MessagingProfileState.Revoked }, cancellationToken).ConfigureAwait(false);
            }

            var current = Volatile.Read(ref _activeProfile);
            if (current is not null && string.Equals(current.Identity.ProfileId, profileId, StringComparison.Ordinal))
            {
                var nextEpoch = Interlocked.Increment(ref _epoch);
                RotateCancellationSource();
                Volatile.Write(ref _hasActiveCall, false);
                _endActiveCallCallback = null;
                await _storage.SetActiveProfileIdAsync(null, cancellationToken).ConfigureAwait(false);
                Volatile.Write(ref _activeProfile, null);
                ActiveProfileChanged?.Invoke(null, nextEpoch);
            }
        }
        finally
        {
            _switchLock.Release();
        }
    }

    public async ValueTask RemoveProfileAsync(string profileId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        await _switchLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = Volatile.Read(ref _activeProfile);
            var wasActive = current is not null && string.Equals(current.Identity.ProfileId, profileId, StringComparison.Ordinal);

            await _storage.DeleteCredentialsAsync(profileId, cancellationToken).ConfigureAwait(false);
            await _storage.DeleteProfileAsync(profileId, cancellationToken).ConfigureAwait(false);

            if (wasActive)
            {
                var nextEpoch = Interlocked.Increment(ref _epoch);
                RotateCancellationSource();
                Volatile.Write(ref _hasActiveCall, false);
                _endActiveCallCallback = null;
                Volatile.Write(ref _activeProfile, null);
                ActiveProfileChanged?.Invoke(null, nextEpoch);
            }
        }
        finally
        {
            _switchLock.Release();
        }
    }

    public async ValueTask UpdateCursorAsync(
        string profileId,
        long expectedEpoch,
        string cursor,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsCurrentEpoch(expectedEpoch, profileId))
        {
            return;
        }

        var existing = await _storage.GetProfileAsync(profileId, cancellationToken).ConfigureAwait(false);
        if (existing is null || !IsCurrentEpoch(expectedEpoch, profileId))
        {
            return;
        }

        var updated = existing with
        {
            LastEventCursor = cursor,
            LastSyncedAtUtc = _timeProvider.GetUtcNow()
        };
        await _storage.SaveProfileAsync(updated, cancellationToken).ConfigureAwait(false);

        if (IsCurrentEpoch(expectedEpoch, profileId))
        {
            Volatile.Write(ref _activeProfile, updated);
        }
    }

    public async ValueTask<string> GetValidAccessTokenAsync(
        string profileId,
        Func<MessagingProfileIdentity, string, CancellationToken, ValueTask<MobileTokenBundle>> refreshCallback,
        bool forceRefresh = false,
        TimeSpan? refreshSkew = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentNullException.ThrowIfNull(refreshCallback);

        var skew = refreshSkew ?? DefaultRefreshSkew;
        var profile = await _storage.GetProfileAsync(profileId, cancellationToken).ConfigureAwait(false)
            ?? throw new MessagingDomainException("profile_not_found", "Messaging profile not found.", 404);

        if (profile.State == MessagingProfileState.Revoked)
        {
            throw new MessagingDomainException("profile_revoked", "Device profile has been revoked.", 401);
        }

        var creds = await _storage.GetCredentialsAsync(profileId, cancellationToken).ConfigureAwait(false)
            ?? throw new MessagingDomainException("credentials_missing", "Profile credentials are unavailable. Re-pair device.", 401);

        var now = _timeProvider.GetUtcNow();
        if (!forceRefresh && creds.AccessTokenExpiresAt > now + skew && !string.IsNullOrWhiteSpace(creds.AccessToken))
        {
            return creds.AccessToken;
        }

        var profileLock = _refreshLocks.GetOrAdd(profileId, _ => new SemaphoreSlim(1, 1));
        await profileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-check after acquiring per-profile single-flight lock so concurrent callers reuse rotated token.
            creds = await _storage.GetCredentialsAsync(profileId, cancellationToken).ConfigureAwait(false)
                ?? throw new MessagingDomainException("credentials_missing", "Profile credentials are unavailable. Re-pair device.", 401);

            now = _timeProvider.GetUtcNow();
            if (!forceRefresh && creds.AccessTokenExpiresAt > now + skew && !string.IsNullOrWhiteSpace(creds.AccessToken))
            {
                return creds.AccessToken;
            }

            if (creds.RefreshTokenExpiresAt <= now || string.IsNullOrWhiteSpace(creds.RefreshToken))
            {
                await _storage.DeleteCredentialsAsync(profileId, cancellationToken).ConfigureAwait(false);
                await _storage.SaveProfileAsync(profile with { State = MessagingProfileState.RequiresReauth }, cancellationToken).ConfigureAwait(false);
                throw new MessagingDomainException("refresh_token_expired", "Session expired. Re-pair this device.", 401);
            }

            MobileTokenBundle rotated;
            try
            {
                rotated = await refreshCallback(profile.Identity, creds.RefreshToken, cancellationToken).ConfigureAwait(false);
            }
            catch (MessagingDomainException ex) when (
                string.Equals(ex.ErrorCode, "refresh_token_reuse_detected", StringComparison.Ordinal)
                || string.Equals(ex.ErrorCode, "device_revoked", StringComparison.Ordinal)
                || ex.StatusCode == 401)
            {
                await MarkProfileRevokedAsync(profileId, cancellationToken).ConfigureAwait(false);
                throw;
            }

            var updatedCreds = new MessagingProfileCredentials(
                rotated.AccessToken,
                rotated.AccessTokenExpiresAt,
                rotated.RefreshToken,
                rotated.RefreshTokenExpiresAt,
                rotated.RefreshGrantId,
                rotated.FamilyId,
                rotated.DeviceId);

            await _storage.SaveCredentialsAsync(profileId, updatedCreds, cancellationToken).ConfigureAwait(false);
            return updatedCreds.AccessToken;
        }
        finally
        {
            profileLock.Release();
        }
    }

    public HttpClient CreateScopedHttpClient(MessagingProfileIdentity profile)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(profile);

        // Disable automatic redirects so Authorization headers can NEVER be forwarded to a different origin.
        var handler = _handlerFactory is not null
            ? _handlerFactory(profile)
            : new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(2)
            };

        return new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = profile.BaseGatewayUrl,
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    private void RotateCancellationSource()
    {
        var previous = Interlocked.Exchange(ref _activeProfileCts, new CancellationTokenSource());
        try
        {
            previous.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            previous.Dispose();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        try
        {
            _activeProfileCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _activeProfileCts.Dispose();
        _switchLock.Dispose();
        foreach (var sem in _refreshLocks.Values)
        {
            sem.Dispose();
        }

        _refreshLocks.Clear();
        return ValueTask.CompletedTask;
    }
}
