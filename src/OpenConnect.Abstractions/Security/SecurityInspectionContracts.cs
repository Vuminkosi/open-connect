using System.Text.Json.Serialization;

namespace OpenConnect.Abstractions;

/// <summary>Versioned schema identifiers for OpenConnect security inspection and evidence contracts.</summary>
public static class SecurityContractVersions
{
    public const string InspectionV1 = "openconnect.security-inspection.v1";
    public const string EvidenceV1 = "openconnect.security-evidence.v1";
    public const string OffsetUnitUnicodeScalar = "unicode-scalar";
}

/// <summary>Distinct lifecycle and runtime verification states for a security capability.</summary>
[JsonConverter(typeof(KebabCaseStringEnumConverter<SecurityCapabilityState>))]
public enum SecurityCapabilityState
{
    Disabled,
    Configured,
    Provisioning,
    Ready,
    Degraded,
    Unavailable
}

/// <summary>Modalities supported by the structured security inspection contract.</summary>
[JsonConverter(typeof(KebabCaseStringEnumConverter<SecurityInspectionModality>))]
public enum SecurityInspectionModality
{
    Text,
    Image,
    File,
    Prompt
}

/// <summary>Outcome status of a security inspection request or individual detector execution.</summary>
[JsonConverter(typeof(SnakeCaseStringEnumConverter<SecurityInspectionStatus>))]
public enum SecurityInspectionStatus
{
    Completed,
    Partial,
    Degraded,
    Unavailable,
    Failed,
    LimitExceeded,
    Unsupported,
    NotRun
}

/// <summary>Discriminated kind of a structured security finding.</summary>
[JsonConverter(typeof(SnakeCaseStringEnumConverter<SecurityFindingKind>))]
public enum SecurityFindingKind
{
    TextSpan,
    ImageRegion,
    Malware,
    Injection
}

/// <summary>Malware scan status returned by file inspection.</summary>
[JsonConverter(typeof(SnakeCaseStringEnumConverter<SecurityMalwareStatus>))]
public enum SecurityMalwareStatus
{
    Clean,
    Detected,
    Unavailable,
    Unsupported,
    LimitExceeded
}

/// <summary>Per-detector execution status reported in a structured security inspection response.</summary>
public sealed record SecurityDetectorStatus(
    string Id,
    string Version,
    SecurityInspectionStatus Status,
    string? ReasonCode = null);

/// <summary>Validated raster image dimensions after orientation normalization.</summary>
public sealed record SecurityImageDimensions(
    int Width,
    int Height,
    string CoordinateSpace = "top-left-pixels",
    bool OrientationNormalized = true);

/// <summary>Axis-aligned bounding box in pixel coordinates relative to normalized image dimensions.</summary>
public sealed record SecurityImageRegion(int X, int Y, int Width, int Height);

/// <summary>Discriminated finding returned by a security worker detector.</summary>
public sealed record SecurityFinding(
    SecurityFindingKind Kind,
    string DetectorId,
    string Category,
    double Confidence,
    int? Start = null,
    int? End = null,
    SecurityImageRegion? Region = null,
    string? Label = null,
    SecurityMalwareStatus? MalwareStatus = null,
    string? ReasonCode = null,
    SecurityPolicyAction? RecommendedAction = null,
    double? Threshold = null,
    string? ModelRevision = null,
    string? SignatureVersion = null);

/// <summary>Request to inspect bounded text with one or more security detectors.</summary>
public sealed record SecurityTextInspectionRequest(
    string RequestId,
    string PolicyRevision,
    IReadOnlyList<string> Detectors,
    string Text,
    IReadOnlySet<string>? RequiredDetectors = null);

/// <summary>Request to inspect bounded prompt/context text for prompt injection.</summary>
public sealed record SecurityPromptInspectionRequest(
    string RequestId,
    string PolicyRevision,
    IReadOnlyList<string> Detectors,
    string Prompt,
    IReadOnlySet<string>? RequiredDetectors = null);

/// <summary>Request to inspect bounded image bytes for OCR text and face regions.</summary>
public sealed record SecurityImageInspectionRequest(
    string RequestId,
    string PolicyRevision,
    IReadOnlyList<string> Detectors,
    ReadOnlyMemory<byte> ImageBytes,
    string ContentType,
    string Sha256Digest,
    IReadOnlySet<string>? RequiredDetectors = null);

/// <summary>Request to inspect bounded file bytes for malware and content identity.</summary>
public sealed record SecurityFileInspectionRequest(
    string RequestId,
    string PolicyRevision,
    IReadOnlyList<string> Detectors,
    ReadOnlyMemory<byte> FileBytes,
    string ContentType,
    string Sha256Digest,
    IReadOnlySet<string>? RequiredDetectors = null);

/// <summary>Structured response from the security worker inspection endpoints.</summary>
public sealed record SecurityInspectionResponse(
    string SchemaVersion,
    string RequestId,
    SecurityInspectionStatus Status,
    IReadOnlyList<SecurityDetectorStatus> Detectors,
    IReadOnlyList<SecurityFinding> Findings,
    string? OffsetUnit = null,
    long DurationMs = 0,
    string? ContentDigest = null,
    SecurityImageDimensions? ImageDimensions = null,
    SecurityMalwareStatus? MalwareStatus = null,
    string? ReasonCode = null);

/// <summary>Verified runtime capability status reported by a security worker.</summary>
public sealed record SecurityCapabilityStatus(
    string Id,
    string Kind,
    SecurityCapabilityState State,
    string Version,
    bool LocalOnly,
    IReadOnlySet<string>? Modalities = null,
    DateTimeOffset? CheckedAt = null,
    string? ReasonCode = null);

/// <summary>Verified snapshot of capabilities reported by a security worker with lease expiration.</summary>
public sealed record SecurityCapabilitiesSnapshot(
    DateTimeOffset VerifiedAt,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<SecurityCapabilityStatus> Capabilities);

/// <summary>Typed, authenticated client for invoking the private OpenConnect Security Worker.</summary>
public interface ISecurityWorkerClient
{
    ValueTask<SecurityCapabilitiesSnapshot> GetCapabilitiesAsync(CancellationToken cancellationToken = default);
    ValueTask<SecurityInspectionResponse> InspectTextAsync(SecurityTextInspectionRequest request, CancellationToken cancellationToken = default);
    ValueTask<SecurityInspectionResponse> InspectPromptAsync(SecurityPromptInspectionRequest request, CancellationToken cancellationToken = default);
    ValueTask<SecurityInspectionResponse> InspectImageAsync(SecurityImageInspectionRequest request, CancellationToken cancellationToken = default);
    ValueTask<SecurityInspectionResponse> InspectFileAsync(SecurityFileInspectionRequest request, CancellationToken cancellationToken = default);
}
