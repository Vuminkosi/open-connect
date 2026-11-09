using System.Text.Json;
using System.Text.Json.Nodes;
using OpenConnect.Abstractions;
using Xunit;

namespace OpenConnect.Abstractions.Tests;

public sealed class CanonicalAgentEventTests
{
    private static readonly DateTimeOffset OccurredAt =
        new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Serialized_envelope_uses_schema_field_names()
    {
        var canonical = CreateCanonical();

        var json = JsonSerializer.Serialize(canonical);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("openconnect.v1", root.GetProperty("schemaVersion").GetString());
        Assert.Equal(canonical.EventId, root.GetProperty("eventId").GetGuid());
        Assert.Equal("agent.completed", root.GetProperty("eventType").GetString());
        Assert.Equal(canonical.SessionId, root.GetProperty("sessionId").GetGuid());
        Assert.Equal(7, root.GetProperty("sequence").GetInt64());
        Assert.Equal(canonical.OccurredAt, root.GetProperty("occurredAt").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Object, root.GetProperty("payload").ValueKind);

        Assert.False(root.TryGetProperty("SchemaVersion", out _));
        Assert.False(root.TryGetProperty("EventId", out _));
        Assert.False(root.TryGetProperty("EventType", out _));
        Assert.False(root.TryGetProperty("SessionId", out _));
        Assert.False(root.TryGetProperty("Sequence", out _));
        Assert.False(root.TryGetProperty("OccurredAt", out _));
        Assert.False(root.TryGetProperty("Payload", out _));
    }

    [Fact]
    public void Constructor_rejects_empty_event_id()
    {
        Assert.Throws<ArgumentException>(() => CreateCanonical(eventId: Guid.Empty));
    }

    [Fact]
    public void Constructor_rejects_empty_session_id()
    {
        Assert.Throws<ArgumentException>(() => CreateCanonical(sessionId: Guid.Empty));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_rejects_non_positive_sequence(long sequence)
    {
        Assert.ThrowsAny<ArgumentException>(() => CreateCanonical(sequence: sequence));
    }

    [Fact]
    public void Constructor_rejects_default_occurred_at()
    {
        Assert.Throws<ArgumentException>(() => CreateCanonical(occurredAt: default(DateTimeOffset)));
    }

    [Fact]
    public void Constructor_rejects_null_payload()
    {
        Assert.Throws<ArgumentNullException>(() => new CanonicalAgentEvent(
            CanonicalAgentEvent.CurrentSchemaVersion,
            Guid.NewGuid(),
            "agent.completed",
            Guid.NewGuid(),
            1,
            OccurredAt,
            null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("COMPLETED")]
    [InlineData("completed")]
    [InlineData("agent completed")]
    public void Constructor_rejects_blank_or_pattern_violating_event_type(string eventType)
    {
        Assert.Throws<ArgumentException>(() => CreateCanonical(eventType: eventType));
    }

    [Fact]
    public void Mapper_preserves_supplied_uuid_and_time()
    {
        var sessionId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var record = new EventRecord(
            new SessionId(Ulid.NewUlid()),
            7,
            "agent.completed",
            """{"outcome":"ok"}""");

        var canonical = CanonicalEventMapper.FromEventRecord(record, sessionId, eventId, OccurredAt, "corr-1");

        Assert.Equal(sessionId, canonical.SessionId);
        Assert.Equal(eventId, canonical.EventId);
        Assert.Equal(OccurredAt, canonical.OccurredAt);
        Assert.Equal("agent.completed", canonical.EventType);
        Assert.Equal(7, canonical.Sequence);
        Assert.Equal("openconnect.v1", canonical.SchemaVersion);
        Assert.Equal("corr-1", canonical.CorrelationId);
        Assert.Equal("ok", canonical.Payload["outcome"]?.GetValue<string>());
    }

    [Fact]
    public void Mapper_does_not_derive_public_session_id_from_record_ulid()
    {
        var record = new EventRecord(
            new SessionId(Ulid.NewUlid()),
            1,
            "agent.started",
            """{"step":1}""");
        var sessionId = Guid.NewGuid();

        var canonical = CanonicalEventMapper.FromEventRecord(record, sessionId, Guid.NewGuid(), OccurredAt);

        Assert.Equal(sessionId, canonical.SessionId);
        Assert.NotEqual(Guid.Empty, canonical.SessionId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void TryFromEventRecord_returns_false_for_non_positive_sequence(long sequence)
    {
        var record = new EventRecord(new SessionId(Ulid.NewUlid()), sequence, "agent.completed", """{"a":1}""");

        Assert.False(CanonicalEventMapper.TryFromEventRecord(
            record, Guid.NewGuid(), Guid.NewGuid(), OccurredAt, out var canonical));
        Assert.Null(canonical);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TryFromEventRecord_returns_false_for_missing_type(string type)
    {
        var record = new EventRecord(new SessionId(Ulid.NewUlid()), 1, type, """{"a":1}""");

        Assert.False(CanonicalEventMapper.TryFromEventRecord(
            record, Guid.NewGuid(), Guid.NewGuid(), OccurredAt, out var canonical));
        Assert.Null(canonical);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("\"just a string\"")]
    public void TryFromEventRecord_returns_false_for_missing_or_non_object_payload(string data)
    {
        var record = new EventRecord(new SessionId(Ulid.NewUlid()), 1, "agent.completed", data);

        Assert.False(CanonicalEventMapper.TryFromEventRecord(
            record, Guid.NewGuid(), Guid.NewGuid(), OccurredAt, out var canonical));
        Assert.Null(canonical);
    }

    [Fact]
    public void TryFromEventRecord_returns_false_without_throwing_for_null_or_empty_identity()
    {
        Assert.False(CanonicalEventMapper.TryFromEventRecord(
            null, Guid.NewGuid(), Guid.NewGuid(), OccurredAt, out var nullCanonical));
        Assert.Null(nullCanonical);

        var record = new EventRecord(new SessionId(Ulid.NewUlid()), 1, "agent.completed", """{"a":1}""");

        Assert.False(CanonicalEventMapper.TryFromEventRecord(
            record, Guid.Empty, Guid.NewGuid(), OccurredAt, out var emptySession));
        Assert.Null(emptySession);

        Assert.False(CanonicalEventMapper.TryFromEventRecord(
            record, Guid.NewGuid(), Guid.Empty, OccurredAt, out var emptyEvent));
        Assert.Null(emptyEvent);

        Assert.False(CanonicalEventMapper.TryFromEventRecord(
            record, Guid.NewGuid(), Guid.NewGuid(), default, out var defaultTime));
        Assert.Null(defaultTime);
    }

    [Fact]
    public void TryParse_returns_false_for_legacy_json_missing_required_fields()
    {
        const string legacyMissingIds = """{"sequence":1,"Type":"agent.completed","Data":"{}"}""";

        Assert.False(CanonicalEventMapper.TryParse(legacyMissingIds, out var canonical));
        Assert.Null(canonical);
    }

    [Fact]
    public void TryParse_returns_false_for_malformed_or_empty_input()
    {
        Assert.False(CanonicalEventMapper.TryParse("{not json", out var malformed));
        Assert.Null(malformed);

        Assert.False(CanonicalEventMapper.TryParse("", out var empty));
        Assert.Null(empty);

        Assert.False(CanonicalEventMapper.TryParse((string?)null, out var missing));
        Assert.Null(missing);
    }

    [Fact]
    public void TryParse_returns_false_for_zero_uuid_and_sequence()
    {
        var json = $@"{{
            ""schemaVersion"": ""openconnect.v1"",
            ""eventId"": ""{Guid.Empty}"",
            ""eventType"": ""agent.completed"",
            ""sessionId"": ""{Guid.Empty}"",
            ""sequence"": 0,
            ""occurredAt"": ""2026-11-08T12:00:00Z"",
            ""payload"": {{}}}}";

        Assert.False(CanonicalEventMapper.TryParse(json, out var canonical));
        Assert.Null(canonical);
    }

    [Fact]
    public void Json_round_trip_preserves_canonical_shape()
    {
        var original = CreateCanonical();

        var json = JsonSerializer.Serialize(original);

        Assert.True(CanonicalEventMapper.TryParse(json, out var parsed));
        Assert.NotNull(parsed);
        Assert.Equal(original.SchemaVersion, parsed!.SchemaVersion);
        Assert.Equal(original.EventId, parsed.EventId);
        Assert.Equal(original.EventType, parsed.EventType);
        Assert.Equal(original.SessionId, parsed.SessionId);
        Assert.Equal(original.Sequence, parsed.Sequence);
        Assert.Equal(original.OccurredAt, parsed.OccurredAt);
        Assert.Equal(original.Payload.ToJsonString(), parsed.Payload.ToJsonString());
    }

    [Fact]
    public void CreateNew_generates_genuine_uuid_and_current_time()
    {
        var before = DateTimeOffset.UtcNow;

        var first = CanonicalAgentEvent.CreateNew(Guid.NewGuid(), "agent.started", 1, new JsonObject());
        var second = CanonicalAgentEvent.CreateNew(first.SessionId, "agent.started", 2, new JsonObject());

        Assert.NotEqual(Guid.Empty, first.EventId);
        Assert.NotEqual(first.EventId, second.EventId);
        Assert.True(first.OccurredAt >= before);
        Assert.True(second.OccurredAt >= before);
    }

    private static CanonicalAgentEvent CreateCanonical(
        Guid? eventId = null,
        Guid? sessionId = null,
        string eventType = "agent.completed",
        long sequence = 7,
        DateTimeOffset? occurredAt = null,
        JsonObject? payload = null)
    {
        return new CanonicalAgentEvent(
            CanonicalAgentEvent.CurrentSchemaVersion,
            eventId ?? Guid.NewGuid(),
            eventType,
            sessionId ?? Guid.NewGuid(),
            sequence,
            occurredAt ?? OccurredAt,
            payload ?? new JsonObject { ["outcome"] = "ok" });
    }
}
