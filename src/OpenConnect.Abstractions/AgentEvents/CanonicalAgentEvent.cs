using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace OpenConnect.Abstractions;

/// <summary>Immutable canonical public projection of an agent event (contract EVENT-V1-PUBLIC).</summary>
/// <remarks>
/// <para>
/// JSON field names honor <c>contracts/jsonschema/event.json</c> exactly:
/// <c>schemaVersion</c>, <c>eventId</c>, <c>eventType</c>, <c>sessionId</c>,
/// <c>sequence</c>, <c>occurredAt</c>, optional <c>correlationId</c>, and <c>payload</c>.
/// </para>
/// <para>
/// The legacy runtime <see cref="EventRecord"/> is untouched. Public identity
/// (<c>eventId</c>, <c>sessionId</c>) and <c>occurredAt</c> are always supplied
/// explicitly by the caller as genuine UUID/time values; a ULID is never
/// converted into a UUID and historical metadata is never fabricated.
/// </para>
/// </remarks>
public sealed record CanonicalAgentEvent
{
    /// <summary>The only schema version accepted by contract EVENT-V1-PUBLIC.</summary>
    public const string CurrentSchemaVersion = "openconnect.v1";

    private static readonly Regex EventTypePattern = new(
        "^[a-z][a-z0-9]*(\\.[a-z][a-z0-9]*)+$",
        RegexOptions.CultureInvariant);

    /// <summary>Creates a validated canonical agent event.</summary>
    /// <exception cref="ArgumentException">Thrown when any value violates the contract.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="payload"/> is null.</exception>
    [JsonConstructor]
    public CanonicalAgentEvent(
        string schemaVersion,
        Guid eventId,
        string eventType,
        Guid sessionId,
        long sequence,
        DateTimeOffset occurredAt,
        JsonObject? payload,
        string? correlationId = null)
    {
        if (!string.Equals(schemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Schema version must be '{CurrentSchemaVersion}'.",
                nameof(schemaVersion));
        }

        if (eventId == Guid.Empty)
        {
            throw new ArgumentException("Event id must be a non-empty UUID.", nameof(eventId));
        }

        if (string.IsNullOrWhiteSpace(eventType))
        {
            throw new ArgumentException("Event type must not be empty.", nameof(eventType));
        }

        if (!EventTypePattern.IsMatch(eventType))
        {
            throw new ArgumentException(
                "Event type must match '<segment>.<segment>[...]' using lowercase alphanumerics.",
                nameof(eventType));
        }

        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("Session id must be a non-empty UUID.", nameof(sessionId));
        }

        if (sequence <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence), "Sequence must be positive.");
        }

        if (occurredAt == default)
        {
            throw new ArgumentException("Occurred-at time must be set.", nameof(occurredAt));
        }

        ArgumentNullException.ThrowIfNull(payload);

        SchemaVersion = schemaVersion;
        EventId = eventId;
        EventType = eventType;
        SessionId = sessionId;
        Sequence = sequence;
        OccurredAt = occurredAt;
        Payload = payload;
        CorrelationId = correlationId;
    }

    /// <summary>Gets the contract schema version (<c>openconnect.v1</c>).</summary>
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; }

    /// <summary>Gets the public event UUID, generated at creation.</summary>
    [JsonPropertyName("eventId")]
    public Guid EventId { get; init; }

    /// <summary>Gets the dotted event type (for example <c>agent.completed</c>).</summary>
    [JsonPropertyName("eventType")]
    public string EventType { get; init; }

    /// <summary>Gets the public session UUID supplied explicitly by the caller.</summary>
    [JsonPropertyName("sessionId")]
    public Guid SessionId { get; init; }

    /// <summary>Gets the 1-based sequence of the event within its session.</summary>
    [JsonPropertyName("sequence")]
    public long Sequence { get; init; }

    /// <summary>Gets the RFC 3339 occurrence time.</summary>
    [JsonPropertyName("occurredAt")]
    public DateTimeOffset OccurredAt { get; init; }

    /// <summary>Gets the optional correlation identifier.</summary>
    [JsonPropertyName("correlationId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CorrelationId { get; init; }

    /// <summary>Gets the event payload object.</summary>
    [JsonPropertyName("payload")]
    public JsonObject Payload { get; init; }

    /// <summary>
    /// Creates a canonical event with a genuine new UUID and the current UTC time.
    /// </summary>
    /// <remarks>
    /// Use this for newly occurring events only. Historical events must supply
    /// their original UUID/time explicitly via the constructor or
    /// <see cref="CanonicalEventMapper"/> instead of fabricating new metadata.
    /// </remarks>
    public static CanonicalAgentEvent CreateNew(
        Guid sessionId,
        string eventType,
        long sequence,
        JsonObject payload,
        string? correlationId = null)
    {
        return new CanonicalAgentEvent(
            CurrentSchemaVersion,
            Guid.NewGuid(),
            eventType,
            sessionId,
            sequence,
            DateTimeOffset.UtcNow,
            payload,
            correlationId);
    }
}
