using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenConnect.Abstractions;

/// <summary>Maps runtime agent events to the canonical public envelope (contract EVENT-V1-PUBLIC).</summary>
/// <remarks>
/// <para>
/// The public <c>sessionId</c>, <c>eventId</c>, and <c>occurredAt</c> values are
/// always taken from the explicit parameters. The runtime
/// <see cref="EventRecord.SessionId"/> ULID is never converted into a UUID and
/// historical metadata is never fabricated: callers replaying history must pass
/// the original UUID/time values through.
/// </para>
/// <para>
/// The throwing <see cref="FromEventRecord"/> method reports contract violations
/// with exceptions. The <c>Try*</c> methods return <c>false</c> (never throw)
/// for legacy records, missing fields, or malformed JSON.
/// </para>
/// </remarks>
public static class CanonicalEventMapper
{
    private static readonly HashSet<string> AllowedFields = new(StringComparer.Ordinal)
    {
        "schemaVersion",
        "eventId",
        "eventType",
        "sessionId",
        "sequence",
        "occurredAt",
        "correlationId",
        "payload",
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Maps a runtime event record to its canonical public envelope.
    /// </summary>
    /// <param name="record">The runtime record supplying type, sequence, and payload data.</param>
    /// <param name="sessionId">The explicit public session UUID (never derived from the record ULID).</param>
    /// <param name="eventId">The explicit public event UUID.</param>
    /// <param name="occurredAt">The explicit occurrence time.</param>
    /// <param name="correlationId">The optional correlation identifier.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="record"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when any value violates the contract.</exception>
    public static CanonicalAgentEvent FromEventRecord(
        EventRecord record,
        Guid sessionId,
        Guid eventId,
        DateTimeOffset occurredAt,
        string? correlationId = null)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (string.IsNullOrWhiteSpace(record.Type))
        {
            throw new ArgumentException("Event type must not be empty.", nameof(record));
        }

        if (record.Sequence <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(record), "Sequence must be positive.");
        }

        var payload = ParsePayload(record);

        return new CanonicalAgentEvent(
            CanonicalAgentEvent.CurrentSchemaVersion,
            eventId,
            record.Type,
            sessionId,
            record.Sequence,
            occurredAt,
            payload,
            correlationId);
    }

    /// <summary>
    /// Attempts to map a runtime event record to its canonical public envelope.
    /// </summary>
    /// <returns><c>true</c> with <paramref name="canonical"/> set; otherwise <c>false</c> without throwing.</returns>
    public static bool TryFromEventRecord(
        EventRecord? record,
        Guid sessionId,
        Guid eventId,
        DateTimeOffset occurredAt,
        out CanonicalAgentEvent? canonical,
        string? correlationId = null)
    {
        canonical = null;

        try
        {
            canonical = FromEventRecord(record!, sessionId, eventId, occurredAt, correlationId);
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

    /// <summary>
    /// Attempts to parse canonical JSON without throwing for legacy or malformed input.
    /// </summary>
    /// <returns><c>true</c> with <paramref name="canonical"/> set; otherwise <c>false</c> without throwing.</returns>
    public static bool TryParse(string? json, out CanonicalAgentEvent? canonical)
    {
        canonical = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return TryParse(document.RootElement, out canonical);
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

    /// <summary>
    /// Attempts to parse a canonical JSON element without throwing for legacy or malformed input.
    /// </summary>
    /// <returns><c>true</c> with <paramref name="canonical"/> set; otherwise <c>false</c> without throwing.</returns>
    public static bool TryParse(JsonElement element, out CanonicalAgentEvent? canonical)
    {
        canonical = null;

        try
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (var property in element.EnumerateObject())
            {
                if (!AllowedFields.Contains(property.Name))
                {
                    return false;
                }
            }

            canonical = JsonSerializer.Deserialize<CanonicalAgentEvent>(element, JsonOptions);
            return canonical is not null;
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

    private static JsonObject ParsePayload(EventRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.Data))
        {
            throw new ArgumentException("Event payload must be a JSON object.", nameof(record));
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(record.Data);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("Event payload must be a JSON object.", nameof(record), exception);
        }

        if (node is not JsonObject payload)
        {
            throw new ArgumentException("Event payload must be a JSON object.", nameof(record));
        }

        return payload;
    }
}
