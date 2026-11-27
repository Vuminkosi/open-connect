namespace OpenConnect.Hosting.AspNetCore.Messaging;

/// <summary>Configuration options for OpenConnect IMessaging gateway endpoints, realtime WebSockets, and follow-up supervision.</summary>
public sealed class MessagingHostingOptions
{
    /// <summary>Whether messaging gateway endpoints are enabled. Defaults to <see langword="false"/> (fail-closed).</summary>
    public bool Enabled { get; set; }

    /// <summary>Application identifier bound to messaging conversations, devices, and tokens.</summary>
    public string ApplicationId { get; set; } = "openconnect";

    /// <summary>Verified company or tenant display name advertised via <c>GET /messaging/compatibility</c>.</summary>
    public string CompanyDisplayName { get; set; } = "OpenConnect";

    /// <summary>Expected token issuer advertised via compatibility and bound into device enrollments.</summary>
    public string Issuer { get; set; } = "https://openconnect.local";

    /// <summary>Expected token audience bound into mobile device tokens.</summary>
    public string Audience { get; set; } = "openconnect-messaging";

    /// <summary>Whether E2EE is required for conversations and voice calls. Defaults to <see langword="true"/>.</summary>
    public bool RequireEncryption { get; set; } = true;

    /// <summary>Whether unencrypted transport-only fallback is allowed when E2EE is unavailable. Defaults to <see langword="false"/>.</summary>
    public bool AllowUnencryptedFallback { get; set; }

    /// <summary>Allowed browser Origin values for cookie-authenticated WebSocket upgrades.</summary>
    public ISet<string> AllowedWebSocketOrigins { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Lifetime of single-use WebSocket authentication tickets (default 30 seconds).</summary>
    public TimeSpan WebSocketTicketLifetime { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Maximum bounded send queue depth per WebSocket connection before disconnecting slow consumers for replay.</summary>
    public int MaxWebSocketQueueDepth { get; set; } = 128;

    /// <summary>Interval between server WebSocket heartbeat frames.</summary>
    public TimeSpan WebSocketHeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Maximum allowed HTTP request body size for messaging JSON endpoints (default 256 KiB).</summary>
    public long MaxRequestBodyBytes { get; set; } = 256 * 1024;

    /// <summary>Maximum allowed encrypted attachment size in bytes (default 25 MiB).</summary>
    public long MaxAttachmentBytes { get; set; } = 25L * 1024 * 1024;

    /// <summary>Maximum page size for paginated list and cursor replay endpoints (default 100).</summary>
    public int MaxReplayPageSize { get; set; } = 100;

    /// <summary>Maximum key package consumption attempts per authenticated caller per <see cref="KeyPackageConsumeWindow"/> (default 30). Non-positive values deny all consumption.</summary>
    public int KeyPackageConsumeLimitPerCaller { get; set; } = 30;

    /// <summary>Maximum key package consumptions against one target (principal or agent) per <see cref="KeyPackageConsumeWindow"/> across all callers (default 60). Non-positive values deny all consumption.</summary>
    public int KeyPackageConsumeLimitPerTarget { get; set; } = 60;

    /// <summary>Fixed window for key package consumption limits (default 1 minute). Non-positive values deny all consumption.</summary>
    public TimeSpan KeyPackageConsumeWindow { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Optional HMAC-SHA256 signing key for opaque scope-bound cursors. If null, a process-local CSPRNG key is used.</summary>
    public byte[]? CursorSigningKey { get; set; }

    /// <summary>Polling interval for <see cref="MessagingFollowUpSupervisorService"/>.</summary>
    public TimeSpan FollowUpPollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Lease duration acquired when claiming due follow-ups.</summary>
    public TimeSpan FollowUpLeaseDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Maximum batch size claimed per supervisor polling cycle.</summary>
    public int FollowUpBatchSize { get; set; } = 20;

    /// <summary>Unique lease owner identifier for this gateway instance.</summary>
    public string FollowUpLeaseOwner { get; set; } = $"gateway-{Environment.MachineName}-{Guid.NewGuid():N}";
}
