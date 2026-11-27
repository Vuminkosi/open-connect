using System.Text.Json;
using OpenConnect.Abstractions;
using Xunit;

namespace OpenConnect.Abstractions.Tests;

public sealed class SecurityContractsTests
{
    [Fact]
    public void Security_enums_serialize_with_expected_wire_names()
    {
        Assert.Equal("\"configured\"", JsonSerializer.Serialize(WorkerReadiness.Configured));
        Assert.Equal("\"malware\"", JsonSerializer.Serialize(SecurityDetectorKind.Malware));
        Assert.Equal("\"malware\"", JsonSerializer.Serialize(SecurityCoverage.Malware));
        Assert.Equal("\"provisioning\"", JsonSerializer.Serialize(SecurityCapabilityState.Provisioning));
        Assert.Equal("\"limit_exceeded\"", JsonSerializer.Serialize(SecurityInspectionStatus.LimitExceeded));
        Assert.Equal("\"not_run\"", JsonSerializer.Serialize(SecurityInspectionStatus.NotRun));
        Assert.Equal("\"text_span\"", JsonSerializer.Serialize(SecurityFindingKind.TextSpan));
        Assert.Equal("\"image_region\"", JsonSerializer.Serialize(SecurityFindingKind.ImageRegion));
        Assert.Equal("\"not_executed\"", JsonSerializer.Serialize(SecurityEnforcementOutcome.NotExecuted));
        Assert.Equal("\"quarantine\"", JsonSerializer.Serialize(SecurityEvidenceDecision.Quarantine));
    }

    [Fact]
    public void Snake_case_enum_converter_rejects_numeric_and_unknown_values()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SecurityEnforcementOutcome>("1"));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SecurityEnforcementOutcome>("\"unknown_future\""));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SecurityInspectionStatus>("0"));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SecurityFindingKind>("\"arbitrary\""));
    }

    [Fact]
    public void SecurityEvidenceEvent_rejects_empty_event_id_and_raw_content_in_identifiers()
    {
        Assert.Throws<ArgumentException>(() => SecurityEvidenceEvent.Create(
            Guid.Empty, "req-1", "tenant-1", "app-1", DateTimeOffset.UtcNow,
            "malware", "upload-release", "clamav", "1.0.0", "rev-1",
            SecurityInspectionStatus.Completed, SecurityEvidenceDecision.Block, SecurityEnforcementOutcome.Prevented,
            "malware_detected"));

        // Raw PII/email or SQL-like strings with spaces/symbols are rejected from content-free identifiers.
        Assert.Throws<ArgumentException>(() => SecurityEvidenceEvent.Create(
            Guid.NewGuid(), "req-1", "tenant-1", "app-1", DateTimeOffset.UtcNow,
            "pii", "model-egress", "rules", "1.0.0", "rev-1",
            SecurityInspectionStatus.Completed, SecurityEvidenceDecision.Block, SecurityEnforcementOutcome.Prevented,
            "user@example.com leaked"));
    }

    [Fact]
    public void SecurityEvidenceEvent_rejects_block_or_quarantine_with_executed_outcome()
    {
        Assert.Throws<ArgumentException>(() => SecurityEvidenceEvent.Create(
            Guid.NewGuid(), "req-1", "tenant-1", "app-1", DateTimeOffset.UtcNow,
            "malware", "upload-release", "clamav", "1.0.0", "rev-1",
            SecurityInspectionStatus.Completed, SecurityEvidenceDecision.Block, SecurityEnforcementOutcome.Executed,
            "malware_detected"));

        Assert.Throws<ArgumentException>(() => SecurityEvidenceEvent.Create(
            Guid.NewGuid(), "req-1", "tenant-1", "app-1", DateTimeOffset.UtcNow,
            "malware", "upload-release", "clamav", "1.0.0", "rev-1",
            SecurityInspectionStatus.Completed, SecurityEvidenceDecision.Quarantine, SecurityEnforcementOutcome.Executed,
            "malware_detected"));
    }

    [Fact]
    public void SecurityEvidenceEvent_computes_deterministic_integrity_digest_and_preserves_distinctions()
    {
        var eventId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var occurredAt = new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.FromHours(2));
        var counts = new Dictionary<string, int> { ["malware"] = 1 };

        var ev1 = SecurityEvidenceEvent.Create(
            eventId, "req-1", "tenant-a", "app-1", occurredAt,
            "malware", "upload-release", "clamav", "1.4.2", "pol-1",
            SecurityInspectionStatus.Completed, SecurityEvidenceDecision.Block, SecurityEnforcementOutcome.Prevented,
            "malware_detected", counts, durationMs: 17, traceId: "trace-1");

        var ev2 = SecurityEvidenceEvent.Create(
            eventId, "req-1", "tenant-a", "app-1", occurredAt.ToUniversalTime(),
            "malware", "upload-release", "clamav", "1.4.2", "pol-1",
            SecurityInspectionStatus.Completed, SecurityEvidenceDecision.Block, SecurityEnforcementOutcome.Prevented,
            "malware_detected", counts, durationMs: 17, traceId: "trace-1");

        Assert.Equal(SecurityContractVersions.EvidenceV1, ev1.SchemaVersion);
        Assert.Equal(ev1.IntegrityDigest, ev2.IntegrityDigest);
        Assert.Equal(64, ev1.IntegrityDigest.Length);
        Assert.Equal(SecurityInspectionStatus.Completed, ev1.InspectionStatus);
        Assert.Equal(SecurityEvidenceDecision.Block, ev1.Decision);
        Assert.Equal(SecurityEnforcementOutcome.Prevented, ev1.EnforcementOutcome);
    }
}
