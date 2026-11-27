using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Messaging;
using OpenConnect.Abstractions.Serialization;

namespace OpenConnect.Client.Messaging;

/// <summary>Compatibility metadata returned by <c>GET /openconnect/v1/messaging/compatibility</c>.</summary>
public sealed record MessagingCompatibilityInfo(
    string ContractVersion,
    string Issuer,
    string ApplicationId,
    string CompanyDisplayName,
    string ApplicationDisplayName,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<ConversationSecurityProfile> SupportedSecurityProfiles);

/// <summary>Request payload for creating a human-inclusive conversation.</summary>
public sealed record CreateMessagingConversationRequest(
    ConversationSecurityProfile SecurityProfile,
    IReadOnlyList<MessagingActor> InitialParticipants);

/// <summary>Request payload for sending an encrypted message envelope.</summary>
public sealed record SendEncryptedMessageRequest(
    EncryptedMessageEnvelope Envelope,
    string ClientOperationId,
    string? CorrelationId = null);

/// <summary>Request payload for recording a monotonic delivery or read receipt.</summary>
public sealed record RecordMessageReceiptRequest(
    MessageReceiptKind Kind,
    string? FailureReason = null);

/// <summary>Request payload for creating a voice/media call invitation.</summary>
public sealed record CreateMessagingCallRequest(
    ConversationId ConversationId,
    CallMediaSecurityMode MediaSecurityMode,
    bool RequireEncryption = true);

/// <summary>Request payload for redeeming a scanned QR enrollment token on mobile.</summary>
public sealed record RedeemEnrollmentRequest(
    string EnrollmentToken,
    DeviceId DeviceId,
    string DevicePublicKeyBase64,
    string DeviceDisplayName,
    DevicePlatform Platform,
    string ChallengeProofSignatureBase64);

/// <summary>Request payload for completing an approved device enrollment.</summary>
public sealed record CompleteEnrollmentRequest(
    DeviceId DeviceId,
    string CompletionProofSignatureBase64,
    string Audience);

/// <summary>Request payload for rotating a device-scoped refresh token.</summary>
public sealed record RefreshMobileTokenRequest(
    string RefreshToken,
    DeviceId DeviceId);

/// <summary>Request payload for registering a push notification token/handle.</summary>
public sealed record RegisterPushRequest(
    PushProviderKind Provider,
    string Token,
    bool ConsentGranted);

/// <summary>Response envelope for cursor-paginated event replay.</summary>
public sealed record MessagingEventReplayResponse(
    IReadOnlyList<MessagingEventEnvelope> Events,
    string? NextCursor,
    bool HasMore);

/// <summary>Typed HTTPS client for OpenConnect IMessaging and Device Enrollment APIs.</summary>
public interface IOpenConnectMessagingClient
{
    ValueTask<MessagingCompatibilityInfo> GetCompatibilityAsync(
        Uri baseGatewayUrl,
        bool allowLoopbackHttp = false,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<OperationalAgentDiscoveryItem>> DiscoverAgentsAsync(
        MessagingProfileIdentity profile,
        string accessToken,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<ConversationRecord>> ListConversationsAsync(
        MessagingProfileIdentity profile,
        string accessToken,
        CancellationToken cancellationToken = default);

    ValueTask<ConversationRecord> CreateConversationAsync(
        MessagingProfileIdentity profile,
        string accessToken,
        CreateMessagingConversationRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<ConversationRecord> GetConversationAsync(
        MessagingProfileIdentity profile,
        string accessToken,
        ConversationId conversationId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<MessageRecord>> ListMessagesAsync(
        MessagingProfileIdentity profile,
        string accessToken,
        ConversationId conversationId,
        string? cursor = null,
        int limit = 50,
        CancellationToken cancellationToken = default);

    ValueTask<MessageCommitResult> SendMessageAsync(
        MessagingProfileIdentity profile,
        string accessToken,
        ConversationId conversationId,
        SendEncryptedMessageRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<MessageReceiptRecord> RecordReceiptAsync(
        MessagingProfileIdentity profile,
        string accessToken,
        MessageId messageId,
        RecordMessageReceiptRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<MessagingCallRecord> CreateCallAsync(
        MessagingProfileIdentity profile,
        string accessToken,
        CreateMessagingCallRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<MessagingCallRecord> AcceptCallAsync(
        MessagingProfileIdentity profile,
        string accessToken,
        CallId callId,
        CancellationToken cancellationToken = default);

    ValueTask<MessagingCallRecord> DeclineCallAsync(
        MessagingProfileIdentity profile,
        string accessToken,
        CallId callId,
        CancellationToken cancellationToken = default);

    ValueTask<MessagingCallRecord> EndCallAsync(
        MessagingProfileIdentity profile,
        string accessToken,
        CallId callId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<FollowUpRecord>> ListFollowUpsAsync(
        MessagingProfileIdentity profile,
        string accessToken,
        CancellationToken cancellationToken = default);

    ValueTask<DeviceEnrollmentCreationResult> InitiateEnrollmentAsync(
        MessagingProfileIdentity profile,
        string accessToken,
        CancellationToken cancellationToken = default);

    ValueTask<DeviceEnrollmentRecord> RedeemEnrollmentAsync(
        Uri baseGatewayUrl,
        RedeemEnrollmentRequest request,
        bool allowLoopbackHttp = false,
        CancellationToken cancellationToken = default);

    ValueTask<DeviceEnrollmentRecord> ConfirmEnrollmentAsync(
        MessagingProfileIdentity profile,
        string accessToken,
        EnrollmentId enrollmentId,
        CancellationToken cancellationToken = default);

    ValueTask<MobileTokenBundle> CompleteEnrollmentAsync(
        Uri baseGatewayUrl,
        EnrollmentId enrollmentId,
        CompleteEnrollmentRequest request,
        bool allowLoopbackHttp = false,
        CancellationToken cancellationToken = default);

    ValueTask<MobileTokenBundle> RefreshTokenAsync(
        MessagingProfileIdentity profile,
        string refreshToken,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<DeviceRecord>> ListDevicesAsync(
        MessagingProfileIdentity profile,
        string accessToken,
        CancellationToken cancellationToken = default);

    ValueTask RevokeDeviceAsync(
        MessagingProfileIdentity profile,
        string accessToken,
        DeviceId deviceId,
        CancellationToken cancellationToken = default);

    ValueTask<PushRegistrationRecord> RegisterPushAsync(
        MessagingProfileIdentity profile,
        string accessToken,
        DeviceId deviceId,
        RegisterPushRequest request,
        CancellationToken cancellationToken = default);

    ValueTask RevokePushAsync(
        MessagingProfileIdentity profile,
        string accessToken,
        DeviceId deviceId,
        CancellationToken cancellationToken = default);

    ValueTask<MessagingEventReplayResponse> ReplayEventsAsync(
        MessagingProfileIdentity profile,
        string accessToken,
        string? cursor = null,
        int limit = 100,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Profile-isolated HTTPS client for OpenConnect IMessaging.
/// Every request uses a per-profile scoped <see cref="HttpClient"/> with redirects disabled
/// and rejects any cross-origin redirect to prevent credential leakage.
/// </summary>
public sealed class OpenConnectMessagingClient : IOpenConnectMessagingClient
{
    private static readonly JsonSerializerOptions JsonOptions = CreateMessagingJsonOptions();

    private readonly IMessagingProfileManager _profileManager;
    private readonly Func<Uri, HttpMessageHandler>? _unauthenticatedHandlerFactory;

    public OpenConnectMessagingClient(
        IMessagingProfileManager profileManager,
        Func<Uri, HttpMessageHandler>? unauthenticatedHandlerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(profileManager);
        _profileManager = profileManager;
        _unauthenticatedHandlerFactory = unauthenticatedHandlerFactory;
    }

    public async ValueTask<MessagingCompatibilityInfo> GetCompatibilityAsync(
        Uri baseGatewayUrl,
        bool allowLoopbackHttp = false,
        CancellationToken cancellationToken = default)
    {
        var normalized = MessagingProfileIdentity.NormalizeGatewayUri(baseGatewayUrl, allowLoopbackHttp);
        using var client = CreateUnauthenticatedClient(normalized);
        using var request = new HttpRequestMessage(HttpMethod.Get, "openconnect/v1/messaging/compatibility");
        return await SendAndReadAsync<MessagingCompatibilityInfo>(client, normalized, request, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<IReadOnlyList<OperationalAgentDiscoveryItem>> DiscoverAgentsAsync(
        MessagingProfileIdentity profile,
        string accessToken,
        CancellationToken cancellationToken = default) =>
        SendProfileAsync<IReadOnlyList<OperationalAgentDiscoveryItem>>(
            profile,
            accessToken,
            HttpMethod.Get,
            "openconnect/v1/agents/discover",
            body: null,
            cancellationToken);

    public ValueTask<IReadOnlyList<ConversationRecord>> ListConversationsAsync(
        MessagingProfileIdentity profile,
        string accessToken,
        CancellationToken cancellationToken = default) =>
        SendProfileAsync<IReadOnlyList<ConversationRecord>>(
            profile,
            accessToken,
            HttpMethod.Get,
            "openconnect/v1/messaging/conversations",
            body: null,
            cancellationToken);

    public ValueTask<ConversationRecord> CreateConversationAsync(
        MessagingProfileIdentity profile,
        string accessToken,
        CreateMessagingConversationRequest request,
        CancellationToken cancellationToken = default) =>
        SendProfileAsync<ConversationRecord>(
            profile,
            accessToken,
            HttpMethod.Post,
            "openconnect/v1/messaging/conversations",
            request,
            cancellationToken);

    public ValueTask<ConversationRecord> GetConversationAsync(
        MessagingProfileIdentity profile,
        string accessToken,
        ConversationId conversationId,
        CancellationToken cancellationToken = default) =>
        SendProfileAsync<ConversationRecord>(
            profile,
            accessToken,
            HttpMethod.Get,
            $"openconnect/v1/messaging/conversations/{conversationId.Value}",
            body: null,
            cancellationToken);

    public ValueTask<IReadOnlyList<MessageRecord>> ListMessagesAsync(
        MessagingProfileIdentity profile,
        string accessToken,
        ConversationId conversationId,
        string? cursor = null,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var clamped = Math.Clamp(limit, 1, 200);
        var path = string.IsNullOrWhiteSpace(cursor)
            ? $"openconnect/v1/messaging/conversations/{conversationId.Value}/messages?limit={clamped}"
            : $"openconnect/v1/messaging/conversations/{conversationId.Value}/messages?cursor={Uri.EscapeDataString(cursor)}&limit={clamped}";

        return SendProfileAsync<IReadOnlyList<MessageRecord>>(
            profile,
            accessToken,
            HttpMethod.Get,
            path,
            body: null,
            cancellationToken);
    }

    public ValueTask<MessageCommitResult> SendMessageAsync(
        MessagingProfileIdentity profile,
        string accessToken,
        ConversationId conversationId,
        SendEncryptedMessageRequest request,
        CancellationToken cancellationToken = default) =>
        SendProfileAsync<MessageCommitResult>(
            profile,
            accessToken,
            HttpMethod.Post,
            $"openconnect/v1/messaging/conversations/{conversationId.Value}/messages",
            request,
            cancellationToken);

    public ValueTask<MessageReceiptRecord> RecordReceiptAsync(
        MessagingProfileIdentity profile,
        string accessToken,
        MessageId messageId,
        RecordMessageReceiptRequest request,
        CancellationToken cancellationToken = default) =>
        SendProfileAsync<MessageReceiptRecord>(
            profile,
            accessToken,
            HttpMethod.Post,
            $"openconnect/v1/messaging/messages/{messageId.Value}/receipts",
            request,
            cancellationToken);

    public ValueTask<MessagingCallRecord> CreateCallAsync(
        MessagingProfileIdentity profile,
        string accessToken,
        CreateMessagingCallRequest request,
        CancellationToken cancellationToken = default) =>
        SendProfileAsync<MessagingCallRecord>(
            profile,
            accessToken,
            HttpMethod.Post,
            "openconnect/v1/messaging/calls",
            request,
            cancellationToken);

    public ValueTask<MessagingCallRecord> AcceptCallAsync(
        MessagingProfileIdentity profile,
        string accessToken,
        CallId callId,
        CancellationToken cancellationToken = default) =>
        SendProfileAsync<MessagingCallRecord>(
            profile,
            accessToken,
            HttpMethod.Post,
            $"openconnect/v1/messaging/calls/{callId.Value}/accept",
            body: null,
            cancellationToken);

    public ValueTask<MessagingCallRecord> DeclineCallAsync(
        MessagingProfileIdentity profile,
        string accessToken,
        CallId callId,
        CancellationToken cancellationToken = default) =>
        SendProfileAsync<MessagingCallRecord>(
            profile,
            accessToken,
            HttpMethod.Post,
            $"openconnect/v1/messaging/calls/{callId.Value}/decline",
            body: null,
            cancellationToken);

    public ValueTask<MessagingCallRecord> EndCallAsync(
        MessagingProfileIdentity profile,
        string accessToken,
        CallId callId,
        CancellationToken cancellationToken = default) =>
        SendProfileAsync<MessagingCallRecord>(
            profile,
            accessToken,
            HttpMethod.Post,
            $"openconnect/v1/messaging/calls/{callId.Value}/end",
            body: null,
            cancellationToken);

    public ValueTask<IReadOnlyList<FollowUpRecord>> ListFollowUpsAsync(
        MessagingProfileIdentity profile,
        string accessToken,
        CancellationToken cancellationToken = default) =>
        SendProfileAsync<IReadOnlyList<FollowUpRecord>>(
            profile,
            accessToken,
            HttpMethod.Get,
            "openconnect/v1/messaging/followups",
            body: null,
            cancellationToken);

    public ValueTask<DeviceEnrollmentCreationResult> InitiateEnrollmentAsync(
        MessagingProfileIdentity profile,
        string accessToken,
        CancellationToken cancellationToken = default) =>
        SendProfileAsync<DeviceEnrollmentCreationResult>(
            profile,
            accessToken,
            HttpMethod.Post,
            "openconnect/v1/devices/enrollment",
            body: null,
            cancellationToken);

    public async ValueTask<DeviceEnrollmentRecord> RedeemEnrollmentAsync(
        Uri baseGatewayUrl,
        RedeemEnrollmentRequest request,
        bool allowLoopbackHttp = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalized = MessagingProfileIdentity.NormalizeGatewayUri(baseGatewayUrl, allowLoopbackHttp);
        using var client = CreateUnauthenticatedClient(normalized);
        using var message = new HttpRequestMessage(HttpMethod.Post, "openconnect/v1/devices/enrollment/redeem")
        {
            Content = JsonContent.Create(request, options: JsonOptions)
        };
        return await SendAndReadAsync<DeviceEnrollmentRecord>(client, normalized, message, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<DeviceEnrollmentRecord> ConfirmEnrollmentAsync(
        MessagingProfileIdentity profile,
        string accessToken,
        EnrollmentId enrollmentId,
        CancellationToken cancellationToken = default) =>
        SendProfileAsync<DeviceEnrollmentRecord>(
            profile,
            accessToken,
            HttpMethod.Post,
            $"openconnect/v1/devices/enrollment/{enrollmentId.Value}/confirm",
            body: null,
            cancellationToken);

    public async ValueTask<MobileTokenBundle> CompleteEnrollmentAsync(
        Uri baseGatewayUrl,
        EnrollmentId enrollmentId,
        CompleteEnrollmentRequest request,
        bool allowLoopbackHttp = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalized = MessagingProfileIdentity.NormalizeGatewayUri(baseGatewayUrl, allowLoopbackHttp);
        using var client = CreateUnauthenticatedClient(normalized);
        using var message = new HttpRequestMessage(HttpMethod.Post, $"openconnect/v1/devices/enrollment/{enrollmentId.Value}/complete")
        {
            Content = JsonContent.Create(request, options: JsonOptions)
        };
        return await SendAndReadAsync<MobileTokenBundle>(client, normalized, message, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<MobileTokenBundle> RefreshTokenAsync(
        MessagingProfileIdentity profile,
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);

        using var client = _profileManager.CreateScopedHttpClient(profile);
        using var message = new HttpRequestMessage(HttpMethod.Post, "openconnect/v1/devices/token/refresh")
        {
            Content = JsonContent.Create(new RefreshMobileTokenRequest(refreshToken, profile.DeviceId), options: JsonOptions)
        };
        return await SendAndReadAsync<MobileTokenBundle>(client, profile.BaseGatewayUrl, message, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<IReadOnlyList<DeviceRecord>> ListDevicesAsync(
        MessagingProfileIdentity profile,
        string accessToken,
        CancellationToken cancellationToken = default) =>
        SendProfileAsync<IReadOnlyList<DeviceRecord>>(
            profile,
            accessToken,
            HttpMethod.Get,
            "openconnect/v1/devices",
            body: null,
            cancellationToken);

    public async ValueTask RevokeDeviceAsync(
        MessagingProfileIdentity profile,
        string accessToken,
        DeviceId deviceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        using var client = _profileManager.CreateScopedHttpClient(profile);
        using var message = new HttpRequestMessage(HttpMethod.Delete, $"openconnect/v1/devices/{deviceId.Value}");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        await SendExpectSuccessAsync(client, profile.BaseGatewayUrl, message, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<PushRegistrationRecord> RegisterPushAsync(
        MessagingProfileIdentity profile,
        string accessToken,
        DeviceId deviceId,
        RegisterPushRequest request,
        CancellationToken cancellationToken = default) =>
        SendProfileAsync<PushRegistrationRecord>(
            profile,
            accessToken,
            HttpMethod.Post,
            $"openconnect/v1/devices/{deviceId.Value}/push",
            request,
            cancellationToken);

    public async ValueTask RevokePushAsync(
        MessagingProfileIdentity profile,
        string accessToken,
        DeviceId deviceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        using var client = _profileManager.CreateScopedHttpClient(profile);
        using var message = new HttpRequestMessage(HttpMethod.Delete, $"openconnect/v1/devices/{deviceId.Value}/push");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        await SendExpectSuccessAsync(client, profile.BaseGatewayUrl, message, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<MessagingEventReplayResponse> ReplayEventsAsync(
        MessagingProfileIdentity profile,
        string accessToken,
        string? cursor = null,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var clamped = Math.Clamp(limit, 1, 500);
        var path = string.IsNullOrWhiteSpace(cursor)
            ? $"openconnect/v1/messaging/events?limit={clamped}"
            : $"openconnect/v1/messaging/events?cursor={Uri.EscapeDataString(cursor)}&limit={clamped}";

        return SendProfileAsync<MessagingEventReplayResponse>(
            profile,
            accessToken,
            HttpMethod.Get,
            path,
            body: null,
            cancellationToken);
    }

    private async ValueTask<TResponse> SendProfileAsync<TResponse>(
        MessagingProfileIdentity profile,
        string accessToken,
        HttpMethod method,
        string relativePath,
        object? body,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        using var client = _profileManager.CreateScopedHttpClient(profile);
        using var message = new HttpRequestMessage(method, relativePath);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (body is not null)
        {
            message.Content = JsonContent.Create(body, options: JsonOptions);
        }

        return await SendAndReadAsync<TResponse>(client, profile.BaseGatewayUrl, message, cancellationToken).ConfigureAwait(false);
    }

    private HttpClient CreateUnauthenticatedClient(Uri baseGatewayUrl)
    {
        var handler = _unauthenticatedHandlerFactory is not null
            ? _unauthenticatedHandlerFactory(baseGatewayUrl)
            : new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(2)
            };

        return new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = baseGatewayUrl,
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    private static async ValueTask<TResponse> SendAndReadAsync<TResponse>(
        HttpClient client,
        Uri expectedOriginUri,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var response = await SendExpectSuccessAsync(client, expectedOriginUri, request, cancellationToken).ConfigureAwait(false);
        var result = await response.Content.ReadFromJsonAsync<TResponse>(JsonOptions, cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            throw new MessagingDomainException("invalid_response", "Gateway returned an empty response payload.", (int)response.StatusCode);
        }

        return result;
    }

    private static async ValueTask<HttpResponseMessage> SendExpectSuccessAsync(
        HttpClient client,
        Uri expectedOriginUri,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        if ((int)response.StatusCode is >= 300 and < 400)
        {
            var location = response.Headers.Location;
            response.Dispose();
            if (location is not null && location.IsAbsoluteUri)
            {
                var expectedOrigin = $"{expectedOriginUri.Scheme.ToLowerInvariant()}://{expectedOriginUri.Authority.ToLowerInvariant()}";
                var actualOrigin = $"{location.Scheme.ToLowerInvariant()}://{location.Authority.ToLowerInvariant()}";
                if (!string.Equals(expectedOrigin, actualOrigin, StringComparison.Ordinal))
                {
                    throw new MessagingDomainException(
                        "redirect_origin_mismatch",
                        "Gateway attempted a cross-origin redirect, which is rejected to protect credentials.",
                        403);
                }
            }

            throw new MessagingDomainException(
                "unexpected_redirect",
                "Gateway returned an unexpected redirect response.",
                403);
        }

        if (!response.IsSuccessStatusCode)
        {
            var statusCode = (int)response.StatusCode;
            var errorCode = statusCode switch
            {
                401 => "unauthorized",
                403 => "forbidden",
                404 => "not_found",
                409 => "conflict",
                410 => "expired",
                413 => "payload_too_large",
                429 => "rate_limited",
                503 => "service_unavailable",
                _ => "gateway_error"
            };
            var message = $"Gateway request failed with HTTP {statusCode}.";

            try
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(body))
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("code", out var codeEl) && codeEl.ValueKind == JsonValueKind.String)
                    {
                        errorCode = codeEl.GetString() ?? errorCode;
                    }

                    if (doc.RootElement.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.String)
                    {
                        message = msgEl.GetString() ?? message;
                    }
                    else if (doc.RootElement.TryGetProperty("title", out var titleEl) && titleEl.ValueKind == JsonValueKind.String)
                    {
                        message = titleEl.GetString() ?? message;
                    }
                }
            }
            catch (JsonException)
            {
            }
            finally
            {
                response.Dispose();
            }

            throw new MessagingDomainException(errorCode, message, statusCode);
        }

        return response;
    }

    internal static JsonSerializerOptions CreateMessagingJsonOptions()
    {
        var options = OpenConnectHttpJson.CreateOptions();
        options.Converters.Add(new MessagingUlidIdentifierJsonConverterFactory());
        return options;
    }

    private sealed class MessagingUlidIdentifierJsonConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) =>
            typeToConvert == typeof(ConversationId)
            || typeToConvert == typeof(MessageId)
            || typeToConvert == typeof(DeviceId)
            || typeToConvert == typeof(CallId)
            || typeToConvert == typeof(EnrollmentId)
            || typeToConvert == typeof(FollowUpId)
            || typeToConvert == typeof(ContinuationId)
            || typeToConvert == typeof(AttachmentId)
            || typeToConvert == typeof(KeyPackageId)
            || typeToConvert == typeof(RefreshGrantId)
            || typeToConvert == typeof(PushRegistrationId)
            || typeToConvert == typeof(ApprovalRequestId);

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(IdentifierConverter<>).MakeGenericType(typeToConvert))!;

        private sealed class IdentifierConverter<TIdentifier> : JsonConverter<TIdentifier>
        {
            private static readonly UlidJsonConverter Inner = new();

            public override TIdentifier Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                var ulid = Inner.Read(ref reader, typeof(Ulid), options);
                return (TIdentifier)Activator.CreateInstance(typeof(TIdentifier), ulid)!;
            }

            public override void Write(Utf8JsonWriter writer, TIdentifier value, JsonSerializerOptions options)
            {
                var ulid = (Ulid)typeof(TIdentifier).GetProperty("Value")!.GetValue(value)!;
                Inner.Write(writer, ulid, options);
            }
        }
    }
}
