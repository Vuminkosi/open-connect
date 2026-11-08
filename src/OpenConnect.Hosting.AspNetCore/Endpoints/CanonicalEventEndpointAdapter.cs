using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenConnect.Abstractions;

namespace OpenConnect.Hosting.AspNetCore;

/// <summary>
/// Maps a public session UUID to its runtime ULID (contract SESSION-UUID-MAPPING, resolver surface only).
/// </summary>
/// <remarks>
/// Implementations resolve the public UUID presented on canonical event routes to the
/// process-local runtime session ULID. A missing mapping must be treated as
/// "session not found" (404) so unmapped UUIDs do not disclose session existence.
/// </remarks>
public interface ISessionUuidResolver
{
    /// <summary>Attempts to resolve a public session UUID to its runtime session ULID.</summary>
    /// <returns><c>true</c> with <paramref name="sessionUlid"/> set; otherwise <c>false</c>.</returns>
    bool TryResolve(Guid sessionUuid, out Ulid sessionUlid);
}

/// <summary>
/// Process-local session UUID mapping for tests and single-instance hosts.
/// </summary>
/// <remarks>
/// <para>
/// NON-DURABLE: registrations live only in memory and are lost on restart.
/// Never present this resolver as restart-stable. Durable EF-backed mapping is
/// owned outside this slice; this type exists so the canonical route has an
/// explicit mapping abstraction without touching persistence in this change.
/// </para>
/// </remarks>
public sealed class InMemorySessionUuidResolver : ISessionUuidResolver
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, Ulid> _byUuid = new();

    /// <summary>Registers a public UUID to runtime ULID mapping.</summary>
    public void Register(Guid sessionUuid, Ulid sessionUlid)
    {
        if (sessionUuid == Guid.Empty) throw new ArgumentException("Session UUID must be non-empty.", nameof(sessionUuid));
        if (sessionUlid == default) throw new ArgumentException("Session ULID must be non-default.", nameof(sessionUlid));
        _byUuid[sessionUuid] = sessionUlid;
    }

    /// <summary>Removes a public UUID mapping when present.</summary>
    /// <returns><c>true</c> when a mapping was removed; otherwise <c>false</c>.</returns>
    public bool Unregister(Guid sessionUuid) => _byUuid.TryRemove(sessionUuid, out _);

    /// <inheritdoc />
    public bool TryResolve(Guid sessionUuid, out Ulid sessionUlid) => _byUuid.TryGetValue(sessionUuid, out sessionUlid);
}

/// <summary>
/// Projects runtime session events to the canonical public envelope (contracts EVENT-V1-PUBLIC + EVENT-CURSOR-V1).
/// </summary>
/// <remarks>
/// <para>
/// Genuine identity only: the public <c>sessionId</c> is the UUID from the request
/// route (resolved to a ULID solely for storage lookup, never converted into a
/// UUID), while the per-event <c>eventId</c> and <c>occurredAt</c> must be stored
/// genuine values carried in the record payload. Missing or malformed identity
/// returns <c>false</c> so the endpoint can answer 422; metadata is never fabricated.
/// </para>
/// <para>
/// Stored convention: a projectable record carries a JSON object payload containing
/// genuine <c>eventId</c> (UUID string) and <c>occurredAt</c> (RFC 3339 date-time)
/// fields plus an optional <c>correlationId</c>. Those identity fields are promoted
/// to the envelope and excluded from the projected <c>payload</c>; every remaining
/// property is passed through untouched.
/// </para>
/// </remarks>
public static class CanonicalEventEndpointAdapter
{
    /// <summary>
    /// Attempts to project a runtime event record to its canonical public envelope.
    /// </summary>
    /// <param name="record">The runtime record supplying sequence, type, and payload data.</param>
    /// <param name="sessionUuid">The genuine public session UUID from the request route.</param>
    /// <param name="canonical">The projected envelope when this method returns <c>true</c>; otherwise <c>null</c>.</param>
    /// <returns><c>true</c> with <paramref name="canonical"/> set; otherwise <c>false</c> without throwing.</returns>
    public static bool TryToCanonical(EventRecord record, Guid sessionUuid, out CanonicalAgentEvent? canonical)
    {
        canonical = null;
        if (sessionUuid == Guid.Empty || record.Sequence <= 0 || string.IsNullOrWhiteSpace(record.Data))
            return false;

        JsonObject? data;
        try
        {
            data = JsonNode.Parse(record.Data) as JsonObject;
        }
        catch (JsonException)
        {
            return false;
        }

        if (data is null)
            return false;

        string? eventIdText;
        string? occurredAtText;
        try
        {
            eventIdText = data.TryGetPropertyValue("eventId", out var idNode) ? idNode?.GetValue<string>() : null;
            occurredAtText = data.TryGetPropertyValue("occurredAt", out var atNode) ? atNode?.GetValue<string>() : null;
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        if (!Guid.TryParse(eventIdText, out var eventId) || eventId == Guid.Empty)
            return false;
        if (!DateTimeOffset.TryParse(occurredAtText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var occurredAt) || occurredAt == default)
            return false;

        string? correlationId = null;
        var correlationPromoted = false;
        if (data.TryGetPropertyValue("correlationId", out var correlationNode)
            && correlationNode is not null
            && correlationNode.GetValueKind() == JsonValueKind.String)
        {
            try
            {
                correlationId = correlationNode.GetValue<string>();
                correlationPromoted = true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        var payload = new JsonObject();
        foreach (var property in data)
        {
            if (property.Key is "eventId" or "occurredAt")
                continue;
            // Only a string correlationId is promoted to the envelope. A
            // non-string value is left in the payload instead of dropped.
            if (property.Key is "correlationId" && correlationPromoted)
                continue;
            payload[property.Key] = property.Value?.DeepClone();
        }

        try
        {
            // Normalize to UTC without altering the stored instant so public
            // serialization emits Zulu ("Z") rather than "+00:00".
            var normalizedOccurredAt = occurredAt.ToUniversalTime();
            canonical = CanonicalEventMapper.FromEventRecord(
                record with { Data = payload.ToJsonString() },
                sessionUuid,
                eventId,
                normalizedOccurredAt,
                correlationId);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException
            || exception is FormatException
            || exception is InvalidOperationException
            || exception is JsonException)
        {
            canonical = null;
            return false;
        }
    }
}
