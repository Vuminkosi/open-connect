using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Messaging;

namespace OpenConnect.IMessaging;

/// <summary>
/// Configuration for the private OpenConnect IMessaging worker bridge.
/// Disabled by default and fail-closed on invalid endpoints, credentials, or encryption policy.
/// </summary>
public sealed class MessagingWorkerOptions
{
    public const int MinSigningKeyBytes = 32;
    public const int DefaultMaxRequestBytes = 256 * 1024;
    public const int DefaultMaxResponseBytes = 512 * 1024;
    public const int HardMaxFrameBytes = 4 * 1024 * 1024;

    /// <summary>Whether the IMessaging bridge is enabled. Defaults to false.</summary>
    public bool Enabled { get; set; }

    /// <summary>Whether the in-app messaging channel is enabled. Defaults to false.</summary>
    public bool InAppEnabled { get; set; }

    /// <summary>Whether E2EE is required for private/agent conversations and encrypted calls. Defaults to true.</summary>
    public bool RequireEncryption { get; set; } = true;

    /// <summary>Explicitly disallows silent fallback to unencrypted transport. Defaults to false.</summary>
    public bool AllowUnencryptedFallback { get; set; }

    /// <summary>Private worker endpoint: either an HTTPS URI or a Unix domain socket path/URI (<c>unix:///path</c>).</summary>
    public string? WorkerEndpoint { get; set; }

    /// <summary>Optional absolute Unix domain socket path (takes precedence if set alongside <see cref="WorkerEndpoint"/>).</summary>
    public string? UnixSocketPath { get; set; }

    /// <summary>Explicit opt-in allowing HTTP on loopback only for isolated development/test fixtures. Defaults to false.</summary>
    public bool AllowInsecureLoopbackHttpInDevelopment { get; set; }

    /// <summary>Bearer token authenticating gateway requests to the private IMessaging worker.</summary>
    public string? BearerToken { get; set; }

    /// <summary>HMAC-SHA256 signing key (>= 32 bytes) used to sign exact serialized request bytes.</summary>
    public byte[]? SigningKey { get; set; }

    /// <summary>Optional key identifier sent via <c>X-OpenConnect-Key-Id</c> for zero-downtime key rotation.</summary>
    public string? SigningKeyId { get; set; }

    /// <summary>Per-request timeout to the private IMessaging worker.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Maximum serialized request payload size in bytes.</summary>
    public int MaxRequestBytes { get; set; } = DefaultMaxRequestBytes;

    /// <summary>Maximum response payload size in bytes.</summary>
    public int MaxResponseBytes { get; set; } = DefaultMaxResponseBytes;

    /// <summary>Maximum bounded follow-up attempts for operational agents.</summary>
    public int MaxFollowUpAttempts { get; set; } = 3;

    /// <summary>Validates options fail-closed when <see cref="Enabled"/> is true.</summary>
    public void Validate()
    {
        if (RequireEncryption && AllowUnencryptedFallback)
            throw new MessagingDomainException(ErrorCodes.InvalidRequest, "Cannot enable AllowUnencryptedFallback while RequireEncryption is true.", 400);

        if (MaxFollowUpAttempts is < 1 or > 20)
            throw new MessagingDomainException(ErrorCodes.InvalidRequest, "MaxFollowUpAttempts must be between 1 and 20.", 400);

        if (!Enabled)
            return;

        if (string.IsNullOrWhiteSpace(BearerToken) || BearerToken.Any(char.IsWhiteSpace))
            throw new MessagingDomainException(ErrorCodes.InvalidRequest, "A non-empty worker BearerToken without whitespace is required when messaging is enabled.", 400);

        if (SigningKey is null || SigningKey.Length < MinSigningKeyBytes)
            throw new MessagingDomainException(ErrorCodes.InvalidRequest, $"A SigningKey of at least {MinSigningKeyBytes} bytes is required when messaging is enabled.", 400);

        if (RequestTimeout <= TimeSpan.Zero || RequestTimeout > TimeSpan.FromMinutes(2))
            throw new MessagingDomainException(ErrorCodes.InvalidRequest, "RequestTimeout must be between 1ms and 2 minutes.", 400);

        if (MaxRequestBytes is < 1024 or > HardMaxFrameBytes)
            throw new MessagingDomainException(ErrorCodes.InvalidRequest, $"MaxRequestBytes must be between 1024 and {HardMaxFrameBytes}.", 400);

        if (MaxResponseBytes is < 1024 or > HardMaxFrameBytes)
            throw new MessagingDomainException(ErrorCodes.InvalidRequest, $"MaxResponseBytes must be between 1024 and {HardMaxFrameBytes}.", 400);

        ResolveEndpoint();
    }

    internal (bool IsUnixSocket, string? SocketPath, Uri? HttpBaseUri) ResolveEndpoint()
    {
        if (!string.IsNullOrWhiteSpace(UnixSocketPath))
        {
            if (!Path.IsPathFullyQualified(UnixSocketPath))
                throw new MessagingDomainException(ErrorCodes.InvalidRequest, "UnixSocketPath must be an absolute path.", 400);
            return (true, UnixSocketPath, null);
        }

        if (string.IsNullOrWhiteSpace(WorkerEndpoint))
            throw new MessagingDomainException(ErrorCodes.InvalidRequest, "Either WorkerEndpoint or UnixSocketPath is required when messaging is enabled.", 400);

        if (WorkerEndpoint.StartsWith("unix://", StringComparison.OrdinalIgnoreCase))
        {
            var path = WorkerEndpoint["unix://".Length..];
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
                throw new MessagingDomainException(ErrorCodes.InvalidRequest, "unix:// WorkerEndpoint must specify an absolute socket path.", 400);
            return (true, path, null);
        }

        if (!Uri.TryCreate(WorkerEndpoint, UriKind.Absolute, out var uri))
            throw new MessagingDomainException(ErrorCodes.InvalidRequest, "WorkerEndpoint must be a valid absolute URI or unix:// path.", 400);

        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.Query))
            throw new MessagingDomainException(ErrorCodes.InvalidRequest, "WorkerEndpoint must not contain credentials, query strings, or fragments.", 400);

        if (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return (false, null, uri);

        if (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            AllowInsecureLoopbackHttpInDevelopment && uri.IsLoopback)
        {
            return (false, null, uri);
        }

        throw new MessagingDomainException(ErrorCodes.InvalidRequest, "WorkerEndpoint must use HTTPS or a protected Unix domain socket.", 400);
    }
}

/// <summary>Configuration for desktop QR device enrollment policy.</summary>
public sealed class DeviceEnrollmentOptions
{
    public bool Enabled { get; set; }
    public TimeSpan EnrollmentExpiry { get; set; } = TimeSpan.FromSeconds(120);
    public bool RequireRecentAuthentication { get; set; } = true;
}

/// <summary>Configuration for authorized operational agent discovery.</summary>
public sealed class AgentDiscoveryOptions
{
    public bool Enabled { get; set; }
    public int MaxPageSize { get; set; } = 50;
}
