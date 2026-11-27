namespace OpenConnect.Samples.Mobile.Services.Notifications;

/// <summary>
/// Platform notification lifecycle and push-availability adapter.
/// Uses no-secret push-off fallback by default when no Firebase client configuration is supplied;
/// direct realtime WSS delivery remains fully operational.
/// </summary>
public interface IMobileNotificationService
{
    bool IsPushConfigured { get; }
    string PushStatusReason { get; }
    ValueTask<bool> RequestNotificationPermissionAsync(CancellationToken cancellationToken = default);
}

public sealed class MobileNotificationService : IMobileNotificationService
{
    public bool IsPushConfigured => false;

    public string PushStatusReason =>
        "Push notifications are disabled in this no-secret build (no google-services.json configured). Direct realtime messaging remains active while connected.";

    public async ValueTask<bool> RequestNotificationPermissionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
#if ANDROID
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            var status = await Permissions.CheckStatusAsync<Permissions.PostNotifications>().ConfigureAwait(false);
            if (status != PermissionStatus.Granted)
            {
                status = await Permissions.RequestAsync<Permissions.PostNotifications>().ConfigureAwait(false);
            }

            return status == PermissionStatus.Granted;
        }
#endif
        return true;
    }
}
