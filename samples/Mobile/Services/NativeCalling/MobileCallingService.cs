using OpenConnect.Client;

namespace OpenConnect.Samples.Mobile.Services.NativeCalling;

/// <summary>
/// Native calling and microphone permission adapter wrapping <see cref="IMauiAudioCaptureService"/>.
/// Never activates the microphone before explicit user consent.
/// </summary>
public interface IMobileCallingService
{
    bool HasMicrophonePermission { get; }
    ValueTask<bool> EnsureMicrophonePermissionAsync(CancellationToken cancellationToken = default);
    IMauiAudioCaptureService AudioCapture { get; }
}

public sealed class MobileCallingService(IMauiAudioCaptureService audioCapture) : IMobileCallingService
{
    public bool HasMicrophonePermission { get; private set; }

    public IMauiAudioCaptureService AudioCapture { get; } = audioCapture;

    public async ValueTask<bool> EnsureMicrophonePermissionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var status = await Permissions.CheckStatusAsync<Permissions.Microphone>().ConfigureAwait(false);
        if (status != PermissionStatus.Granted)
        {
            status = await Permissions.RequestAsync<Permissions.Microphone>().ConfigureAwait(false);
        }

        HasMicrophonePermission = status == PermissionStatus.Granted;
        return HasMicrophonePermission;
    }
}
