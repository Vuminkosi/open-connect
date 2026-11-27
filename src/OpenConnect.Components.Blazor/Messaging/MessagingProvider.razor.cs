using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Abstractions.Messaging;
using OpenConnect.Client.Messaging;
using OpenConnect.Components.Contracts.Messaging;

namespace OpenConnect.Components.Blazor.Messaging;

/// <summary>
/// User- and profile-scoped messaging provider whose realtime connection lifetime is bound to the
/// signed-in user / active profile — NOT to composer open/close state.
/// Disposes cleanly on logout or profile switch and fences all callbacks by profile epoch.
/// </summary>
public partial class MessagingProvider : ComponentBase, IAsyncDisposable
{
    private readonly List<MessagingConversationSummary> _conversations = [];
    private readonly List<MessagingToastNotification> _toasts = [];
    private readonly HashSet<string> _seenEventIds = new(StringComparer.Ordinal);
    private long _boundEpoch;
    private string? _boundProfileId;
    private bool _disposed;

    private IMessagingProfileManager? _profileManager;

    [Inject] private IServiceProvider Services { get; set; } = null!;

    /// <summary>Host content rendered inside the messaging provider boundary.</summary>
    [Parameter] public RenderFragment? ChildContent { get; set; }

    /// <summary>Whether the host currently has an authenticated user session.</summary>
    [Parameter] public bool IsAuthenticated { get; set; } = true;

    /// <summary>Current connection state of the user-scoped realtime stream.</summary>
    public MessagingConnectionState ConnectionState { get; private set; } = MessagingConnectionState.Disconnected;

    /// <summary>Sanitized connection or revocation error message, if any.</summary>
    public string? ConnectionStatusMessage { get; private set; }

    /// <summary>Currently active profile epoch bound by this provider.</summary>
    public long BoundProfileEpoch => _boundEpoch;

    /// <summary>Currently active profile ID bound by this provider.</summary>
    public string? BoundProfileId => _boundProfileId;

    /// <summary>Total unread messages across conversations for the active profile.</summary>
    public int TotalUnreadCount => _conversations.Sum(c => Math.Max(0, c.UnreadCount));

    /// <summary>Current inbox conversations for the active profile.</summary>
    public IReadOnlyList<MessagingConversationSummary> Conversations => _conversations;

    /// <summary>Active non-intrusive incoming message toasts.</summary>
    public IReadOnlyList<MessagingToastNotification> Toasts => _toasts;

    /// <summary>Current pending incoming call invitation, if any.</summary>
    public MessagingCallInvitation? PendingIncomingCall { get; private set; }

    /// <summary>Current active voice/media call, if any.</summary>
    public MessagingActiveCall? ActiveCall { get; private set; }

    /// <summary>Raised whenever provider state changes so child components can refresh.</summary>
    public event Action? StateChanged;

    protected override void OnInitialized()
    {
        _profileManager = Services.GetService<IMessagingProfileManager>();
        if (_profileManager is not null)
        {
            _profileManager.ActiveProfileChanged += OnActiveProfileChanged;
            BindToProfile(_profileManager.ActiveProfile, _profileManager.CurrentEpoch);
        }
        else if (IsAuthenticated)
        {
            // Single-company web host mode: bound directly to the authenticated company web session
            // without multi-company mobile profile switching.
            _boundEpoch = 1;
            _boundProfileId = "host-web-session";
            ConnectionState = MessagingConnectionState.Connected;
        }
    }

    protected override void OnParametersSet()
    {
        if (!IsAuthenticated)
        {
            ClearSessionState(MessagingConnectionState.Disconnected, null);
        }
    }

    /// <summary>Updates connection status only if the callback matches the active profile epoch.</summary>
    public bool TryUpdateConnectionState(long epoch, string profileId, MessagingConnectionState state, string? message = null)
    {
        if (!IsCurrentBinding(epoch, profileId))
        {
            return false;
        }

        ConnectionState = state;
        ConnectionStatusMessage = message;
        NotifyStateChanged();
        return true;
    }

    /// <summary>Replaces inbox conversations for the active profile if the epoch matches.</summary>
    public bool TrySetConversations(long epoch, string profileId, IEnumerable<MessagingConversationSummary> conversations)
    {
        if (!IsCurrentBinding(epoch, profileId))
        {
            return false;
        }

        _conversations.Clear();
        _conversations.AddRange(conversations);
        NotifyStateChanged();
        return true;
    }

    /// <summary>
    /// Applies a realtime event envelope with profile-epoch fencing and event-ID deduplication.
    /// Returns <see langword="false"/> if dropped due to stale epoch or duplicate event ID.
    /// </summary>
    public bool TryApplyEvent(long epoch, string profileId, MessagingEventEnvelope envelope, MessagingToastNotification? toast = null)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (!IsCurrentBinding(epoch, profileId))
        {
            return false;
        }

        if (!_seenEventIds.Add(envelope.EventId))
        {
            return false;
        }

        if (string.Equals(envelope.Type, MessagingEventTypes.DeviceRevoked, StringComparison.Ordinal))
        {
            ConnectionState = MessagingConnectionState.Revoked;
            ConnectionStatusMessage = "This device was revoked. Re-pair from desktop to reconnect.";
            PendingIncomingCall = null;
            ActiveCall = null;
            NotifyStateChanged();
            return true;
        }

        if (toast is not null)
        {
            _toasts.Insert(0, toast);
            if (_toasts.Count > 5)
            {
                _toasts.RemoveAt(_toasts.Count - 1);
            }
        }

        NotifyStateChanged();
        return true;
    }

    /// <summary>Sets or clears a pending incoming call invitation (never auto-answers).</summary>
    public bool TrySetPendingIncomingCall(long epoch, string profileId, MessagingCallInvitation? invitation)
    {
        if (!IsCurrentBinding(epoch, profileId))
        {
            return false;
        }

        PendingIncomingCall = invitation;
        NotifyStateChanged();
        return true;
    }

    /// <summary>Sets or clears the active call and updates the profile manager's active-call switch guard.</summary>
    public bool TrySetActiveCall(long epoch, string profileId, MessagingActiveCall? activeCall, Func<CancellationToken, ValueTask>? endCallCallback = null)
    {
        if (!IsCurrentBinding(epoch, profileId))
        {
            return false;
        }

        ActiveCall = activeCall;
        _profileManager?.SetActiveCallState(activeCall is not null, endCallCallback);
        NotifyStateChanged();
        return true;
    }

    /// <summary>Dismisses an incoming message toast by ID.</summary>
    public void DismissToast(string notificationId)
    {
        var removed = _toasts.RemoveAll(t => string.Equals(t.NotificationId, notificationId, StringComparison.Ordinal));
        if (removed > 0)
        {
            NotifyStateChanged();
        }
    }

    /// <summary>Disposes active session state on explicit logout.</summary>
    public async ValueTask LogoutAsync(CancellationToken cancellationToken = default)
    {
        if (_profileManager is not null)
        {
            await _profileManager.DeactivateCurrentProfileAsync(cancellationToken).ConfigureAwait(false);
        }

        ClearSessionState(MessagingConnectionState.Disconnected, null);
    }

    private bool IsCurrentBinding(long epoch, string profileId)
    {
        if (_disposed || !IsAuthenticated)
        {
            return false;
        }

        if (_profileManager is not null && !_profileManager.IsCurrentEpoch(epoch, profileId))
        {
            return false;
        }

        return _boundEpoch == epoch && string.Equals(_boundProfileId, profileId, StringComparison.Ordinal);
    }

    private void OnActiveProfileChanged(MessagingProfileRecord? profile, long epoch)
    {
        if (_disposed)
        {
            return;
        }

        BindToProfile(profile, epoch);
        _ = InvokeAsync(StateHasChanged);
    }

    private void BindToProfile(MessagingProfileRecord? profile, long epoch)
    {
        _boundEpoch = epoch;
        _boundProfileId = profile?.Identity.ProfileId;
        _conversations.Clear();
        _toasts.Clear();
        _seenEventIds.Clear();
        PendingIncomingCall = null;
        ActiveCall = null;

        if (profile is null)
        {
            ConnectionState = MessagingConnectionState.Disconnected;
            ConnectionStatusMessage = null;
        }
        else if (profile.State == MessagingProfileState.Revoked)
        {
            ConnectionState = MessagingConnectionState.Revoked;
            ConnectionStatusMessage = "This device was revoked. Re-pair from desktop to reconnect.";
        }
        else
        {
            ConnectionState = MessagingConnectionState.Connecting;
            ConnectionStatusMessage = null;
        }

        NotifyStateChanged();
    }

    private void ClearSessionState(MessagingConnectionState state, string? message)
    {
        _conversations.Clear();
        _toasts.Clear();
        _seenEventIds.Clear();
        PendingIncomingCall = null;
        ActiveCall = null;
        ConnectionState = state;
        ConnectionStatusMessage = message;
        NotifyStateChanged();
    }

    private void NotifyStateChanged() => StateChanged?.Invoke();

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        if (_profileManager is not null)
        {
            _profileManager.ActiveProfileChanged -= OnActiveProfileChanged;
        }

        ClearSessionState(MessagingConnectionState.Disconnected, null);
        return ValueTask.CompletedTask;
    }
}
