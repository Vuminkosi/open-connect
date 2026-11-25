using OpenConnect.Abstractions.Messaging;
using OpenConnect.Client.Messaging;

namespace OpenConnect.Samples.Mobile.Services.QrScanner;

/// <summary>Camera permission and QR enrollment payload parsing service for OpenConnect Mobile.</summary>
public interface IMobileQrScannerService
{
    ValueTask<bool> EnsureCameraPermissionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates a scanned or pasted QR payload using <see cref="MessagingQrEnrollmentValidator"/>.
    /// Rejects non-HTTPS, embedded credentials, fragments, expired tokens, and malformed payloads.
    /// </summary>
    DeviceEnrollmentQrPayload ValidateScannedPayload(string rawQrPayload);
}

public sealed class MobileQrScannerService : IMobileQrScannerService
{
    public async ValueTask<bool> EnsureCameraPermissionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var status = await Permissions.CheckStatusAsync<Permissions.Camera>().ConfigureAwait(false);
        if (status == PermissionStatus.Granted)
        {
            return true;
        }

        status = await Permissions.RequestAsync<Permissions.Camera>().ConfigureAwait(false);
        return status == PermissionStatus.Granted;
    }

    public DeviceEnrollmentQrPayload ValidateScannedPayload(string rawQrPayload) =>
        MessagingQrEnrollmentValidator.ParseAndValidate(rawQrPayload, DateTimeOffset.UtcNow, allowLoopbackHttp: false);
}
