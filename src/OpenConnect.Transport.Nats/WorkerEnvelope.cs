using System.Text.Json;

namespace OpenConnect.Transport.Nats;

/// <summary>Validated, metadata-only worker request; task content remains in the referenced payload store.</summary>
public sealed record WorkerEnvelope(string SchemaVersion, Guid MessageId, string TaskId, string Kind,
    DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt, WorkerLease Lease, string PayloadRef, string? CorrelationId = null)
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { PropertyNameCaseInsensitive = true };
    public static WorkerEnvelope Parse(ReadOnlySpan<byte> json)
    {
        using var document = JsonDocument.Parse(json.ToArray());
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("Envelope must be an object.");
        string[] allowed = ["schemaVersion", "messageId", "taskId", "kind", "issuedAt", "expiresAt", "lease", "payloadRef", "correlationId"];
        foreach (var property in root.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal)) throw new JsonException("Envelope contains an unsupported field.");
        if (root.GetProperty("schemaVersion").GetString() != "openconnect.v1") throw new JsonException("Unsupported envelope version.");
        var envelope = JsonSerializer.Deserialize<WorkerEnvelope>(json, SerializerOptions) ?? throw new JsonException("Envelope is required.");
        if (envelope.MessageId == Guid.Empty || string.IsNullOrWhiteSpace(envelope.TaskId) ||
            string.IsNullOrWhiteSpace(envelope.Kind) || envelope.Lease.LeaseId == Guid.Empty || envelope.Lease.FencingToken < 1 ||
            string.IsNullOrWhiteSpace(envelope.PayloadRef) || envelope.PayloadRef.Length > 2048 || envelope.ExpiresAt <= envelope.IssuedAt)
            throw new JsonException("Envelope fields are invalid.");
        return envelope;
    }
}

public sealed record WorkerLease(Guid LeaseId, long FencingToken);
