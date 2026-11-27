using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Messaging;

namespace OpenConnect.Client.Messaging;

/// <summary>Lifecycle state of a locally stored mobile or desktop messaging profile.</summary>
public enum MessagingProfileState
{
    Active = 0,
    Inactive = 1,
    RequiresReauth = 2,
    Revoked = 3
}

/// <summary>
/// Immutable identity tuple for an enrolled company messaging profile.
/// Uniquely scopes credentials, crypto state, local cache, push registration, cursor, and HTTP clients.
/// </summary>
public sealed record MessagingProfileIdentity
{
    public MessagingProfileIdentity(
        string profileId,
        Uri baseGatewayUrl,
        TenantId tenantId,
        string applicationId,
        string issuer,
        string subject,
        DeviceId deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentNullException.ThrowIfNull(baseGatewayUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        ProfileId = profileId.Trim();
        BaseGatewayUrl = NormalizeGatewayUri(baseGatewayUrl, allowLoopbackHttp: false);
        TenantId = tenantId;
        ApplicationId = applicationId.Trim();
        Issuer = issuer.Trim();
        Subject = subject.Trim();
        DeviceId = deviceId;
    }

    internal MessagingProfileIdentity(
        string profileId,
        Uri baseGatewayUrl,
        TenantId tenantId,
        string applicationId,
        string issuer,
        string subject,
        DeviceId deviceId,
        bool allowLoopbackHttp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentNullException.ThrowIfNull(baseGatewayUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        ProfileId = profileId.Trim();
        BaseGatewayUrl = NormalizeGatewayUri(baseGatewayUrl, allowLoopbackHttp);
        TenantId = tenantId;
        ApplicationId = applicationId.Trim();
        Issuer = issuer.Trim();
        Subject = subject.Trim();
        DeviceId = deviceId;
    }

    public string ProfileId { get; }

    public Uri BaseGatewayUrl { get; }

    public TenantId TenantId { get; }

    public string ApplicationId { get; }

    public string Issuer { get; }

    public string Subject { get; }

    public DeviceId DeviceId { get; }

    /// <summary>Derives a stable local opaque ProfileId from the canonical identity tuple.</summary>
    public static string ComputeStableProfileId(
        Uri baseGatewayUrl,
        TenantId tenantId,
        string applicationId,
        string issuer,
        string subject,
        DeviceId deviceId,
        bool allowLoopbackHttp = false)
    {
        var normalized = NormalizeGatewayUri(baseGatewayUrl, allowLoopbackHttp);
        var material = string.Join(
            "|",
            normalized.AbsoluteUri.ToLowerInvariant(),
            tenantId.Value.ToString(),
            applicationId.Trim(),
            issuer.Trim(),
            subject.Trim(),
            deviceId.Value.ToString());
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return "prof_" + Convert.ToHexString(hash[..16]).ToLowerInvariant();
    }

    /// <summary>Creates a profile identity using a deterministic local opaque ProfileId.</summary>
    public static MessagingProfileIdentity Create(
        Uri baseGatewayUrl,
        TenantId tenantId,
        string applicationId,
        string issuer,
        string subject,
        DeviceId deviceId,
        bool allowLoopbackHttp = false)
    {
        var profileId = ComputeStableProfileId(
            baseGatewayUrl,
            tenantId,
            applicationId,
            issuer,
            subject,
            deviceId,
            allowLoopbackHttp);
        return new MessagingProfileIdentity(
            profileId,
            baseGatewayUrl,
            tenantId,
            applicationId,
            issuer,
            subject,
            deviceId,
            allowLoopbackHttp);
    }

    /// <summary>
    /// Validates and normalizes a gateway base URL.
    /// Rejects non-HTTPS (unless explicitly allowed for isolated loopback test fixtures),
    /// embedded userinfo, query strings, fragments, and path traversal segments.
    /// </summary>
    public static Uri NormalizeGatewayUri(Uri uri, bool allowLoopbackHttp = false)
    {
        ArgumentNullException.ThrowIfNull(uri);

        if (!uri.IsAbsoluteUri)
        {
            throw new MessagingDomainException("invalid_gateway_url", "Gateway URL must be absolute.", 400);
        }

        var isHttps = string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        var isAllowedLoopbackHttp = allowLoopbackHttp
            && string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && uri.IsLoopback;

        if (!isHttps && !isAllowedLoopbackHttp)
        {
            throw new MessagingDomainException("insecure_gateway_url", "Gateway URL must use HTTPS.", 400);
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new MessagingDomainException("invalid_gateway_url", "Gateway URL must not contain embedded credentials.", 400);
        }

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new MessagingDomainException("invalid_gateway_url", "Gateway URL must not contain query parameters or fragments.", 400);
        }

        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new MessagingDomainException("invalid_gateway_url", "Gateway URL must specify a valid host.", 400);
        }

        if (uri.AbsolutePath.Contains("..", StringComparison.Ordinal)
            || uri.OriginalString.Contains("..", StringComparison.Ordinal)
            || uri.OriginalString.Contains('\\'))
        {
            throw new MessagingDomainException("invalid_gateway_url", "Gateway URL contains an invalid path.", 400);
        }

        var builder = new UriBuilder(uri)
        {
            Path = uri.AbsolutePath.TrimEnd('/') + "/",
            Query = string.Empty,
            Fragment = string.Empty
        };

        return builder.Uri;
    }

    /// <summary>Returns the canonical origin (scheme + host + port) for redirect-origin checks.</summary>
    public string CanonicalOrigin => $"{BaseGatewayUrl.Scheme.ToLowerInvariant()}://{BaseGatewayUrl.Authority.ToLowerInvariant()}";
}

/// <summary>Device-scoped credentials stored exclusively in platform-protected per-profile secure storage.</summary>
public sealed record MessagingProfileCredentials(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    RefreshGrantId RefreshGrantId,
    string FamilyId,
    DeviceId DeviceId);

/// <summary>Public (non-secret) metadata for a locally stored messaging profile.</summary>
public sealed record MessagingProfileRecord(
    MessagingProfileIdentity Identity,
    string CompanyDisplayName,
    string ApplicationDisplayName,
    MessagingProfileState State,
    string? LastEventCursor = null,
    DateTimeOffset? LastSyncedAtUtc = null,
    int UnreadCount = 0,
    bool PushConsentGranted = false,
    string? PushRegistrationHandle = null);

/// <summary>Outcome of a serialized active-profile switch request.</summary>
public sealed record MessagingProfileSwitchResult(
    bool Succeeded,
    string? ActiveProfileId,
    long ProfileEpoch,
    bool ActiveCallConfirmationRequired = false,
    string? ErrorCode = null,
    string? ErrorMessage = null);

/// <summary>Strict validator for desktop-to-mobile QR enrollment payloads.</summary>
public static class MessagingQrEnrollmentValidator
{
    public const int SupportedVersion = 1;
    public const string ExpectedType = "openconnect.device.enrollment";
    public const int MaxPayloadBytes = 4096;

    /// <summary>
    /// Parses and strictly validates a raw QR JSON string.
    /// Rejects oversized payloads, wrong schema version/type, non-HTTPS URLs, userinfo,
    /// query/fragment, expired tokens, and short/empty enrollment tokens.
    /// </summary>
    public static DeviceEnrollmentQrPayload ParseAndValidate(
        string rawQrPayload,
        DateTimeOffset utcNow,
        bool allowLoopbackHttp = false)
    {
        if (string.IsNullOrWhiteSpace(rawQrPayload))
        {
            throw new MessagingDomainException("invalid_qr_payload", "QR payload is empty.", 400);
        }

        if (Encoding.UTF8.GetByteCount(rawQrPayload) > MaxPayloadBytes)
        {
            throw new MessagingDomainException("invalid_qr_payload", "QR payload exceeds maximum allowed size.", 413);
        }

        DeviceEnrollmentQrPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<DeviceEnrollmentQrPayload>(
                rawQrPayload,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException ex)
        {
            throw new MessagingDomainException("invalid_qr_payload", "QR payload is not valid JSON.", 400, ex);
        }

        if (payload is null)
        {
            throw new MessagingDomainException("invalid_qr_payload", "QR payload could not be parsed.", 400);
        }

        Validate(payload, utcNow, allowLoopbackHttp);
        return payload;
    }

    /// <summary>Validates a parsed QR enrollment payload.</summary>
    public static void Validate(
        DeviceEnrollmentQrPayload payload,
        DateTimeOffset utcNow,
        bool allowLoopbackHttp = false)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (payload.Version != SupportedVersion)
        {
            throw new MessagingDomainException("unsupported_qr_version", $"Unsupported QR enrollment version {payload.Version}.", 400);
        }

        if (!string.Equals(payload.Type, ExpectedType, StringComparison.Ordinal))
        {
            throw new MessagingDomainException("invalid_qr_type", "QR payload type is not a valid OpenConnect device enrollment.", 400);
        }

        if (!Uri.TryCreate(payload.BaseGatewayUrl, UriKind.Absolute, out var gatewayUri))
        {
            throw new MessagingDomainException("invalid_gateway_url", "QR gateway URL is malformed.", 400);
        }

        _ = MessagingProfileIdentity.NormalizeGatewayUri(gatewayUri, allowLoopbackHttp);

        if (string.IsNullOrWhiteSpace(payload.TenantId)
            || string.IsNullOrWhiteSpace(payload.ApplicationId)
            || string.IsNullOrWhiteSpace(payload.PairingId))
        {
            throw new MessagingDomainException("invalid_qr_payload", "QR payload is missing required tenant, application, or pairing identifiers.", 400);
        }

        if (string.IsNullOrWhiteSpace(payload.EnrollmentToken) || payload.EnrollmentToken.Length < 16)
        {
            throw new MessagingDomainException("invalid_qr_payload", "QR enrollment token is missing or too short.", 400);
        }

        if (payload.ExpiresAt <= utcNow)
        {
            throw new MessagingDomainException("enrollment_expired", "QR enrollment code has expired. Generate a new code on desktop.", 410);
        }
    }
}
