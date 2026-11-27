using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Messaging;
using OpenConnect.Hosting.AspNetCore.Messaging;
using OpenConnect.Samples.Blazor.Models;

namespace OpenConnect.Samples.Blazor.Messaging;

/// <summary>
/// Issues and validates real HMAC-SHA256 signed, device-bound mobile JWT access tokens
/// and 256-bit CSPRNG rotating refresh tokens. Immediately rejects revoked devices.
/// </summary>
public sealed class TodoMobileTokenIssuer : IMobileTokenIssuer
{
    private static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromDays(14);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly byte[] _signingKey;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<MessagingHostingOptions> _hostingOptions;
    private readonly TimeProvider _timeProvider;

    public TodoMobileTokenIssuer(
        IConfiguration configuration,
        IServiceScopeFactory scopeFactory,
        IOptions<MessagingHostingOptions> hostingOptions,
        TimeProvider? timeProvider = null)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _hostingOptions = hostingOptions ?? throw new ArgumentNullException(nameof(hostingOptions));
        _timeProvider = timeProvider ?? TimeProvider.System;

        var configuredKey = configuration["OpenConnect:Messaging:MobileTokenSigningKey"];
        if (!string.IsNullOrWhiteSpace(configuredKey))
        {
            var bytes = Encoding.UTF8.GetBytes(configuredKey);
            _signingKey = bytes.Length >= 32 ? bytes : SHA256.HashData(bytes);
        }
        else
        {
            // Deterministic development key derived from machine/app scope if not explicitly configured,
            // or random 32-byte key in memory.
            _signingKey = SHA256.HashData(Encoding.UTF8.GetBytes($"openconnect.samples.blazor.mobile.jwt.v1|{Environment.MachineName}"));
        }
    }

    public ValueTask<MobileTokenBundle> IssueAsync(
        TenantId tenantId,
        string applicationId,
        PrincipalId principalId,
        DeviceId deviceId,
        string issuer,
        string subject,
        string audience,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var familyId = $"fam_{Ulid.NewUlid()}";
        return ValueTask.FromResult(CreateBundle(tenantId, applicationId, principalId, deviceId, issuer, subject, audience, familyId));
    }

    public ValueTask<MobileTokenBundle> RotateAsync(
        RefreshGrantRecord existingGrant,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(existingGrant);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(CreateBundle(
            existingGrant.TenantId,
            existingGrant.ApplicationId,
            existingGrant.PrincipalId,
            existingGrant.DeviceId,
            existingGrant.Issuer,
            existingGrant.Subject,
            existingGrant.Audience,
            existingGrant.FamilyId));
    }

    /// <summary>
    /// Validates a device-bound HMAC-SHA256 JWT access token and checks live device revocation state in SQLite.
    /// </summary>
    public async Task<MobileTokenValidationResult> ValidateAccessTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            return MobileTokenValidationResult.Fail("missing_token", "Bearer token is empty.");

        var parts = token.Split('.');
        if (parts.Length != 3)
            return MobileTokenValidationResult.Fail("malformed_token", "JWT structure is invalid.");

        var signingInput = $"{parts[0]}.{parts[1]}";
        if (!TryBase64UrlDecode(parts[2], out var providedSig))
            return MobileTokenValidationResult.Fail("invalid_signature_encoding", "JWT signature encoding is invalid.");

        var expectedSig = HMACSHA256.HashData(_signingKey, Encoding.UTF8.GetBytes(signingInput));
        if (providedSig.Length != expectedSig.Length || !CryptographicOperations.FixedTimeEquals(providedSig, expectedSig))
            return MobileTokenValidationResult.Fail("invalid_signature", "JWT signature verification failed.");

        if (!TryBase64UrlDecode(parts[1], out var payloadBytes))
            return MobileTokenValidationResult.Fail("invalid_payload", "JWT payload encoding is invalid.");

        MobileJwtPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<MobileJwtPayload>(payloadBytes, JsonOptions);
        }
        catch (JsonException)
        {
            return MobileTokenValidationResult.Fail("invalid_payload_json", "JWT payload JSON is invalid.");
        }

        if (payload is null)
            return MobileTokenValidationResult.Fail("empty_payload", "JWT payload is empty.");

        var nowUnix = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        if (payload.Exp <= nowUnix)
            return MobileTokenValidationResult.Fail("token_expired", "Mobile access token has expired.");

        var hosting = _hostingOptions.Value;
        if (!string.Equals(payload.Aud, hosting.Audience, StringComparison.Ordinal) ||
            !string.Equals(payload.AppId, hosting.ApplicationId, StringComparison.Ordinal))
        {
            return MobileTokenValidationResult.Fail("invalid_audience", "Token audience or application does not match host.");
        }

        Ulid tenantUlid, principalUlid, deviceUlid;
        try
        {
            tenantUlid = Ulid.Parse(payload.TenantId);
            principalUlid = Ulid.Parse(payload.Sub);
            deviceUlid = Ulid.Parse(payload.DeviceId);
        }
        catch (FormatException)
        {
            return MobileTokenValidationResult.Fail("invalid_claims", "Token identity claims are not canonical ULIDs.");
        }

        var tenantId = new TenantId(tenantUlid);
        var principalId = new PrincipalId(principalUlid);
        var deviceId = new DeviceId(deviceUlid);

        // Live device revocation check against SQLite
        await using var scope = _scopeFactory.CreateAsyncScope();
        var deviceStore = scope.ServiceProvider.GetRequiredService<IDeviceEnrollmentStore>();
        var device = await deviceStore.GetDeviceAsync(tenantId, deviceId, cancellationToken).ConfigureAwait(false);
        if (device is null || device.Status != DeviceStatus.Verified || device.PrincipalId != principalId)
            return MobileTokenValidationResult.Fail(ErrorCodes.DeviceRevoked, "Device is revoked or not verified.");

        var seedPrincipal = TodoDirectorySeed.FindPrincipal(tenantId, principalId);
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.Value.ToString()),
            new(ClaimTypes.NameIdentifier, principalId.Value.ToString()),
            new("sub", principalId.Value.ToString()),
            new("device_id", deviceId.Value.ToString()),
            new("app_id", payload.AppId),
            new("iss", payload.Iss),
            new("aud", payload.Aud),
            new("identity_mode", "mobile_device")
        };
        if (seedPrincipal is not null)
        {
            claims.Add(new Claim(ClaimTypes.Role, seedPrincipal.Role));
            claims.Add(new Claim(ClaimTypes.Name, seedPrincipal.DisplayName));
        }

        var identity = new ClaimsIdentity(claims, "OpenConnectMobileDevice");
        return MobileTokenValidationResult.Ok(new ClaimsPrincipal(identity), tenantId, principalId, deviceId);
    }

    private MobileTokenBundle CreateBundle(
        TenantId tenantId,
        string applicationId,
        PrincipalId principalId,
        DeviceId deviceId,
        string issuer,
        string subject,
        string audience,
        string familyId)
    {
        var now = _timeProvider.GetUtcNow();
        var accessExpires = now.Add(AccessTokenLifetime);
        var refreshExpires = now.Add(RefreshTokenLifetime);

        var headerJson = """{"alg":"HS256","typ":"JWT"}""";
        var payload = new MobileJwtPayload(
            Iss: issuer,
            Sub: principalId.Value.ToString(),
            Aud: audience,
            TenantId: tenantId.Value.ToString(),
            DeviceId: deviceId.Value.ToString(),
            AppId: applicationId,
            OriginalSub: subject,
            Iat: now.ToUnixTimeSeconds(),
            Exp: accessExpires.ToUnixTimeSeconds(),
            Jti: Ulid.NewUlid().ToString());

        var headerB64 = Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson));
        var payloadB64 = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions));
        var signingInput = $"{headerB64}.{payloadB64}";
        var sig = HMACSHA256.HashData(_signingKey, Encoding.UTF8.GetBytes(signingInput));
        var jwt = $"{signingInput}.{Base64UrlEncode(sig)}";

        var refreshBytes = RandomNumberGenerator.GetBytes(32);
        var refreshToken = Base64UrlEncode(refreshBytes);
        CryptographicOperations.ZeroMemory(refreshBytes);

        return new MobileTokenBundle(
            AccessToken: jwt,
            AccessTokenExpiresAt: accessExpires,
            RefreshToken: refreshToken,
            RefreshTokenExpiresAt: refreshExpires,
            RefreshGrantId: new RefreshGrantId(Ulid.NewUlid()),
            FamilyId: familyId,
            DeviceId: deviceId);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryBase64UrlDecode(string input, out byte[] bytes)
    {
        bytes = [];
        var padded = input.Replace('-', '+').Replace('_', '/');
        var mod = padded.Length % 4;
        if (mod == 1)
            return false;
        if (mod > 0)
            padded += new string('=', 4 - mod);
        try
        {
            bytes = Convert.FromBase64String(padded);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private sealed record MobileJwtPayload(
        string Iss,
        string Sub,
        string Aud,
        string TenantId,
        string DeviceId,
        string AppId,
        string OriginalSub,
        long Iat,
        long Exp,
        string Jti);
}

public sealed record MobileTokenValidationResult(
    bool Succeeded,
    ClaimsPrincipal? Principal,
    TenantId TenantId,
    PrincipalId PrincipalId,
    DeviceId DeviceId,
    string? ErrorCode,
    string? FailureReason)
{
    public static MobileTokenValidationResult Ok(ClaimsPrincipal principal, TenantId tenantId, PrincipalId principalId, DeviceId deviceId) =>
        new(true, principal, tenantId, principalId, deviceId, null, null);

    public static MobileTokenValidationResult Fail(string errorCode, string reason) =>
        new(false, null, default, default, default, errorCode, reason);
}
