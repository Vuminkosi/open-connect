using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Messaging;

namespace OpenConnect.Hosting.AspNetCore.Messaging;

/// <summary>Encodes and verifies HMAC-SHA256 signed opaque cursors bound to tenant, principal, device, and scope.</summary>
public sealed class MessagingCursorCodec
{
    private readonly byte[] _key;

    public MessagingCursorCodec(IOptions<MessagingHostingOptions>? options = null)
    {
        var configured = options?.Value.CursorSigningKey;
        if (configured is { Length: >= 32 })
        {
            _key = [.. configured];
        }
        else
        {
            _key = RandomNumberGenerator.GetBytes(32);
        }
    }

    public string Encode(TenantId tenantId, PrincipalId principalId, DeviceId? deviceId, string scope, long sequence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);

        var payload = $"{tenantId.Value}|{principalId.Value}|{deviceId?.Value.ToString() ?? "-"}|{scope}|{sequence}";
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var mac = HMACSHA256.HashData(_key, payloadBytes);
        return $"{Base64UrlEncode(payloadBytes)}.{Base64UrlEncode(mac)}";
    }

    public bool TryDecode(
        string cursor,
        TenantId expectedTenantId,
        PrincipalId expectedPrincipalId,
        DeviceId? expectedDeviceId,
        string expectedScope,
        out long sequence)
    {
        sequence = 0;
        if (string.IsNullOrWhiteSpace(cursor) || cursor.Length > 512)
            return false;

        var dotIndex = cursor.IndexOf('.');
        if (dotIndex <= 0 || dotIndex != cursor.LastIndexOf('.') || dotIndex == cursor.Length - 1)
            return false;

        if (!TryBase64UrlDecode(cursor[..dotIndex], out var payloadBytes) ||
            !TryBase64UrlDecode(cursor[(dotIndex + 1)..], out var providedMac))
            return false;

        var expectedMac = HMACSHA256.HashData(_key, payloadBytes);
        if (providedMac.Length != expectedMac.Length ||
            !CryptographicOperations.FixedTimeEquals(providedMac, expectedMac))
            return false;

        var parts = Encoding.UTF8.GetString(payloadBytes).Split('|');
        if (parts.Length != 5)
            return false;

        var expectedDeviceText = expectedDeviceId?.Value.ToString() ?? "-";
        if (!string.Equals(parts[0], expectedTenantId.Value.ToString(), StringComparison.Ordinal) ||
            !string.Equals(parts[1], expectedPrincipalId.Value.ToString(), StringComparison.Ordinal) ||
            !string.Equals(parts[2], expectedDeviceText, StringComparison.Ordinal) ||
            !string.Equals(parts[3], expectedScope, StringComparison.Ordinal) ||
            !long.TryParse(parts[4], out var parsedSequence) ||
            parsedSequence < 0)
        {
            return false;
        }

        sequence = parsedSequence;
        return true;
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
}

/// <summary>In-memory single-use short-lived WebSocket ticket store with atomic consumption and expiry.</summary>
public sealed class InMemoryMessagingWebSocketTicketService : IMessagingWebSocketTicketService
{
    private readonly ConcurrentDictionary<string, MessagingWebSocketTicket> _tickets = new(StringComparer.Ordinal);
    private readonly IOptions<MessagingHostingOptions> _options;
    private readonly TimeProvider _timeProvider;

    public InMemoryMessagingWebSocketTicketService(
        IOptions<MessagingHostingOptions> options,
        TimeProvider? timeProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ValueTask<MessagingWebSocketTicket> IssueAsync(
        TenantId tenantId,
        string applicationId,
        PrincipalId principalId,
        DeviceId? deviceId,
        string? allowedOrigin,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = _timeProvider.GetUtcNow();
        PurgeExpired(now);

        var lifetime = _options.Value.WebSocketTicketLifetime;
        if (lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromMinutes(2))
            lifetime = TimeSpan.FromSeconds(30);

        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(tokenBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var ticket = new MessagingWebSocketTicket(
            token,
            tenantId,
            applicationId,
            principalId,
            deviceId,
            string.IsNullOrWhiteSpace(allowedOrigin) ? null : allowedOrigin.Trim(),
            now.Add(lifetime));

        _tickets[token] = ticket;
        return ValueTask.FromResult(ticket);
    }

    public ValueTask<MessagingWebSocketTicket?> TryConsumeAsync(
        string ticket,
        string? requestOrigin,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(ticket) || ticket.Length > 128)
            return ValueTask.FromResult<MessagingWebSocketTicket?>(null);

        if (!_tickets.TryRemove(ticket, out var record))
            return ValueTask.FromResult<MessagingWebSocketTicket?>(null);

        var now = _timeProvider.GetUtcNow();
        if (now >= record.ExpiresAtUtc)
            return ValueTask.FromResult<MessagingWebSocketTicket?>(null);

        if (record.AllowedOrigin is not null &&
            !string.Equals(record.AllowedOrigin, requestOrigin?.Trim(), StringComparison.OrdinalIgnoreCase))
            return ValueTask.FromResult<MessagingWebSocketTicket?>(null);

        return ValueTask.FromResult<MessagingWebSocketTicket?>(record);
    }

    private void PurgeExpired(DateTimeOffset now)
    {
        foreach (var kvp in _tickets)
        {
            if (now >= kvp.Value.ExpiresAtUtc)
                _tickets.TryRemove(kvp.Key, out _);
        }
    }
}

/// <summary>Process-local realtime connection hub with bounded per-connection queues and immediate device revocation teardown.</summary>
public sealed class MessagingRealtimeHub : IMessagingRealtimeHub
{
    private readonly ConcurrentDictionary<Guid, RealtimeConnection> _connections = new();
    private readonly IOptions<MessagingHostingOptions> _options;

    public MessagingRealtimeHub(IOptions<MessagingHostingOptions> options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public int ActiveConnectionCount => _connections.Count;

    internal RealtimeConnection RegisterConnection(
        TenantId tenantId,
        PrincipalId principalId,
        DeviceId? deviceId,
        CancellationToken connectionToken)
    {
        var capacity = Math.Clamp(_options.Value.MaxWebSocketQueueDepth, 4, 2048);
        var conn = new RealtimeConnection(Guid.NewGuid(), tenantId, principalId, deviceId, capacity, connectionToken);
        _connections[conn.ConnectionId] = conn;
        return conn;
    }

    internal void UnregisterConnection(Guid connectionId)
    {
        if (_connections.TryRemove(connectionId, out var conn))
        {
            conn.Complete();
            conn.Dispose();
        }
    }

    public ValueTask PublishToPrincipalAsync(
        TenantId tenantId,
        PrincipalId principalId,
        MessagingEventEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        cancellationToken.ThrowIfCancellationRequested();

        if (envelope.TenantId != tenantId)
            throw new MessagingDomainException(ErrorCodes.Unauthorized, "Cross-tenant event delivery is forbidden.", 403);

        foreach (var conn in _connections.Values)
        {
            if (conn.TenantId == tenantId && conn.PrincipalId == principalId)
            {
                if (!conn.TryEnqueue(envelope))
                {
                    // Slow consumer: fail closed and disconnect so client performs bounded cursor replay.
                    conn.RequestClose("slow_consumer_replay_required");
                }
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<int> CloseDeviceConnectionsAsync(
        TenantId tenantId,
        DeviceId deviceId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var closed = 0;
        foreach (var conn in _connections.Values)
        {
            if (conn.TenantId == tenantId && conn.DeviceId == deviceId)
            {
                conn.RequestClose(string.IsNullOrWhiteSpace(reason) ? "device_revoked" : reason);
                closed++;
            }
        }

        return ValueTask.FromResult(closed);
    }

    internal sealed class RealtimeConnection : IDisposable
    {
        private readonly Channel<MessagingEventEnvelope> _channel;
        private readonly CancellationTokenSource _cts;
        private string? _closeReason;

        public RealtimeConnection(
            Guid connectionId,
            TenantId tenantId,
            PrincipalId principalId,
            DeviceId? deviceId,
            int maxQueueDepth,
            CancellationToken linkedToken)
        {
            ConnectionId = connectionId;
            TenantId = tenantId;
            PrincipalId = principalId;
            DeviceId = deviceId;
            _channel = Channel.CreateBounded<MessagingEventEnvelope>(new BoundedChannelOptions(maxQueueDepth)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });
            _cts = CancellationTokenSource.CreateLinkedTokenSource(linkedToken);
        }

        public Guid ConnectionId { get; }
        public TenantId TenantId { get; }
        public PrincipalId PrincipalId { get; }
        public DeviceId? DeviceId { get; }
        public ChannelReader<MessagingEventEnvelope> Reader => _channel.Reader;
        public CancellationToken Token => _cts.Token;
        public string? CloseReason => Volatile.Read(ref _closeReason);

        public bool TryEnqueue(MessagingEventEnvelope envelope) => _channel.Writer.TryWrite(envelope);

        public void RequestClose(string reason)
        {
            Interlocked.CompareExchange(ref _closeReason, reason, null);
            _channel.Writer.TryComplete();
            try { _cts.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        public void Complete() => _channel.Writer.TryComplete();

        public void Dispose() => _cts.Dispose();
    }
}

/// <summary>Default voice admission broker wrapping <see cref="InMemorySessionStore"/> to bind accepted calls to short-lived voice sessions.</summary>
public sealed class DefaultMessagingVoiceAdmissionBroker : IMessagingVoiceAdmissionBroker
{
    private readonly InMemorySessionStore _sessionStore;
    private readonly TimeProvider _timeProvider;

    public DefaultMessagingVoiceAdmissionBroker(InMemorySessionStore sessionStore, TimeProvider? timeProvider = null)
    {
        _sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ValueTask<MessagingVoiceAdmission> IssueAdmissionAsync(
        TenantId tenantId,
        PrincipalId principalId,
        DeviceId deviceId,
        CallId callId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var session = _sessionStore.Create(tenantId, principalId);
        var rawToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken))).ToLowerInvariant();
        var expiresAt = _timeProvider.GetUtcNow().AddMinutes(2);
        return ValueTask.FromResult(new MessagingVoiceAdmission(session.Id, rawToken, tokenHash, expiresAt));
    }
}
