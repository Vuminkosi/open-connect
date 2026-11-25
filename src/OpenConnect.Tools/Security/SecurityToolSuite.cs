using System.Text;
using System.Text.Json;
using OpenConnect.Abstractions;

namespace OpenConnect.Tools.Security;

/// <summary>
/// Registers and executes the 8 canonical host-brokered agent security tools (FW-090) through
/// <see cref="ToolCatalog"/>, <see cref="ToolBroker"/>, and <see cref="ToolAuthorizationService"/>.
/// </summary>
public sealed class SecurityToolSuite
{
    private static readonly IReadOnlySet<string> AllowedTextDetectors = new HashSet<string>(StringComparer.Ordinal)
    {
        "rules",
        "gliner-pii"
    };

    private static readonly IReadOnlySet<string> AllowedImageDetectors = new HashSet<string>(StringComparer.Ordinal)
    {
        "paddle-ocr",
        "yunet",
        "rules",
        "gliner-pii"
    };

    private readonly ISecurityWorkerClient? _workerClient;
    private readonly ISecurityCapabilityHealthProvider _healthProvider;
    private readonly SecurityToolPolicyOptions _options;
    private readonly ISecurityArtifactResolver? _artifactResolver;
    private readonly ISecurityContentRedactor? _contentRedactor;
    private readonly ISecurityPolicyAssessmentProvider? _policyAssessmentProvider;
    private readonly ISecurityEvidenceLookup? _evidenceLookup;
    private readonly ISecurityEvidenceSink? _evidenceSink;
    private readonly ISecurityToolOutputSanitizer? _outputSanitizer;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a new <see cref="SecurityToolSuite"/> backed by host-trusted services.</summary>
    public SecurityToolSuite(
        ISecurityCapabilityHealthProvider healthProvider,
        SecurityToolPolicyOptions options,
        ISecurityWorkerClient? workerClient = null,
        ISecurityArtifactResolver? artifactResolver = null,
        ISecurityContentRedactor? contentRedactor = null,
        ISecurityPolicyAssessmentProvider? policyAssessmentProvider = null,
        ISecurityEvidenceLookup? evidenceLookup = null,
        ISecurityEvidenceSink? evidenceSink = null,
        ISecurityToolOutputSanitizer? outputSanitizer = null,
        TimeProvider? timeProvider = null)
    {
        _healthProvider = healthProvider ?? throw new ArgumentNullException(nameof(healthProvider));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _workerClient = workerClient;
        _artifactResolver = artifactResolver;
        _contentRedactor = contentRedactor;
        _policyAssessmentProvider = policyAssessmentProvider;
        _evidenceLookup = evidenceLookup;
        _evidenceSink = evidenceSink;
        _outputSanitizer = outputSanitizer;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Returns static metadata (schemas, descriptions, modes, capabilities) for all 8 canonical security tools.</summary>
    public static IReadOnlyList<SecurityToolMetadata> GetAllToolMetadata() =>
    [
        new(
            new ToolId(SecurityToolIds.ScanText),
            "Security Scan Text",
            "inspect",
            "[INSPECT - read-only] Scans bounded text for sensitive entities and returns content-free entity categories, counts, coverage, and safe findings. Does not modify content or authorize access.",
            """{"type":"object","properties":{"text":{"type":"string"},"detectors":{"type":"array"}},"required":["text"],"additionalProperties":false}""",
            RequiresApproval: false,
            SecurityToolAccessPolicy.GetBackingCapabilities(SecurityToolIds.ScanText)),
        new(
            new ToolId(SecurityToolIds.ScanImage),
            "Security Scan Image",
            "inspect",
            "[INSPECT - read-only] Scans an authorized tenant-scoped image artifact reference for text and face regions. Does not modify the image or authorize release.",
            """{"type":"object","properties":{"artifact_ref":{"type":"string"},"detectors":{"type":"array"}},"required":["artifact_ref"],"additionalProperties":false}""",
            RequiresApproval: false,
            SecurityToolAccessPolicy.GetBackingCapabilities(SecurityToolIds.ScanImage)),
        new(
            new ToolId(SecurityToolIds.ScanFile),
            "Security Scan File",
            "inspect",
            "[INSPECT - read-only] Scans an authorized tenant-scoped file artifact reference for malware and returns status and evidence ID. Does not release or modify quarantined files.",
            """{"type":"object","properties":{"artifact_ref":{"type":"string"}},"required":["artifact_ref"],"additionalProperties":false}""",
            RequiresApproval: false,
            SecurityToolAccessPolicy.GetBackingCapabilities(SecurityToolIds.ScanFile)),
        new(
            new ToolId(SecurityToolIds.ScanPrompt),
            "Security Scan Prompt",
            "inspect",
            "[INSPECT - read-only] Inspects bounded prompt or context text for prompt-injection signals and returns content-free classification, coverage, and evidence ID. Does not grant tool authority.",
            """{"type":"object","properties":{"text":{"type":"string"}},"required":["text"],"additionalProperties":false}""",
            RequiresApproval: false,
            SecurityToolAccessPolicy.GetBackingCapabilities(SecurityToolIds.ScanPrompt)),
        new(
            new ToolId(SecurityToolIds.RedactContent),
            "Security Redact Content",
            "enforce",
            "[ENFORCE - host mutation] Applies an approved host redaction operation to an authorized tenant-scoped artifact reference and returns a sanitized artifact reference and host decision.",
            """{"type":"object","properties":{"artifact_ref":{"type":"string"},"operation":{"type":"string"}},"required":["artifact_ref","operation"],"additionalProperties":false}""",
            RequiresApproval: true,
            SecurityToolAccessPolicy.GetBackingCapabilities(SecurityToolIds.RedactContent)),
        new(
            new ToolId(SecurityToolIds.CheckDestination),
            "Security Check Destination",
            "inspect",
            "[INSPECT - policy assessment only] Evaluates host egress policy for a registered service ID and operation ID. Returns policy assessment only; does NOT authorize or execute any outbound call.",
            """{"type":"object","properties":{"service_id":{"type":"string"},"operation_id":{"type":"string"}},"required":["service_id","operation_id"],"additionalProperties":false}""",
            RequiresApproval: false,
            SecurityToolAccessPolicy.GetBackingCapabilities(SecurityToolIds.CheckDestination)),
        new(
            new ToolId(SecurityToolIds.CheckDataAccess),
            "Security Check Data Access",
            "inspect",
            "[INSPECT - policy assessment only] Evaluates host data-access policy for a registered data operation ID. Returns scoped policy assessment only; never accepts raw SQL or executes queries.",
            """{"type":"object","properties":{"data_operation_id":{"type":"string"}},"required":["data_operation_id"],"additionalProperties":false}""",
            RequiresApproval: false,
            SecurityToolAccessPolicy.GetBackingCapabilities(SecurityToolIds.CheckDataAccess)),
        new(
            new ToolId(SecurityToolIds.ExplainEvent),
            "Security Explain Event",
            "inspect",
            "[INSPECT - read-only] Looks up a tenant-scoped, content-free security evidence event by evidence ID and returns reason code and enforcement provenance.",
            """{"type":"object","properties":{"evidence_id":{"type":"string"}},"required":["evidence_id"],"additionalProperties":false}""",
            RequiresApproval: false,
            SecurityToolAccessPolicy.GetBackingCapabilities(SecurityToolIds.ExplainEvent))
    ];

    /// <summary>Registers all 8 canonical security tools in the supplied <see cref="ToolCatalog"/> for the specified agent.</summary>
    public void RegisterAll(ToolCatalog catalog, AgentId ownerAgentId, IEnumerable<AgentId>? allowedAgents = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var agents = allowedAgents?.ToArray() ?? [ownerAgentId];

        foreach (var meta in GetAllToolMetadata())
        {
            Func<JsonElement, CancellationToken, ValueTask<JsonElement>> handler = meta.ToolId.Value switch
            {
                SecurityToolIds.ScanText => HandleScanTextAsync,
                SecurityToolIds.ScanImage => HandleScanImageAsync,
                SecurityToolIds.ScanFile => HandleScanFileAsync,
                SecurityToolIds.ScanPrompt => HandleScanPromptAsync,
                SecurityToolIds.RedactContent => HandleRedactContentAsync,
                SecurityToolIds.CheckDestination => HandleCheckDestinationAsync,
                SecurityToolIds.CheckDataAccess => HandleCheckDataAccessAsync,
                SecurityToolIds.ExplainEvent => HandleExplainEventAsync,
                _ => throw new InvalidOperationException($"Unsupported security tool '{meta.ToolId.Value}'.")
            };

            var descriptor = new ToolDescriptor(meta.ToolId, meta.Name, ownerAgentId);
            catalog.Register(new ToolRegistration(descriptor, meta.JsonSchema, handler, meta.RequiresApproval), agents);
        }
    }

    private async ValueTask<JsonElement> HandleScanTextAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var ctx = RequireContext();
        var text = RequireBoundedText(arguments, "text");
        var detectors = ParseOptionalDetectors(arguments, AllowedTextDetectors, ["rules"]);

        await RequireCapabilitiesReadyAsync(ctx.TenantId, detectors, cancellationToken).ConfigureAwait(false);
        if (_workerClient is null)
            throw new ToolAuthorizationException(ErrorCodes.CapabilityUnavailable);

        var requestId = Guid.NewGuid().ToString("D");
        SecurityInspectionResponse response;
        try
        {
            response = await _workerClient.InspectTextAsync(
                new SecurityTextInspectionRequest(
                    requestId,
                    _options.PolicyRevision,
                    detectors,
                    text,
                    new HashSet<string>(detectors, StringComparer.Ordinal)),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { throw new ToolAuthorizationException(ErrorCodes.CapabilityUnavailable); }

        EnsureCompletedResponse(response, detectors);
        var categoryCounts = CountFindingCategories(response.Findings);
        var eventId = await TryRecordEvidenceAsync(
            ctx,
            requestId,
            "pii_text",
            "agent-security-tool",
            detectors[0],
            ResolveDetectorVersion(response, detectors[0]),
            response.Status,
            SecurityEvidenceDecision.Allow,
            SecurityEnforcementOutcome.Executed,
            categoryCounts.Count > 0 ? "sensitive_detected" : "clean",
            categoryCounts,
            response.DurationMs,
            cancellationToken).ConfigureAwait(false);

        var safeFindings = response.Findings
            .Where(f => f.Kind == SecurityFindingKind.TextSpan)
            .Select(f => new
            {
                detector_id = f.DetectorId,
                category = f.Category,
                confidence = f.Confidence,
                start = f.Start,
                end = f.End
            })
            .ToArray();

        return await SanitizeOutputAsync(ctx, new
        {
            mode = "inspect",
            authorization_granted = false,
            status = "completed",
            request_id = requestId,
            evidence_id = eventId.ToString("D"),
            detectors = response.Detectors.Select(d => new { id = d.Id, version = d.Version, status = d.Status.ToString().ToLowerInvariant() }).ToArray(),
            category_counts = categoryCounts,
            finding_count = safeFindings.Length,
            findings = safeFindings
        }, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<JsonElement> HandleScanImageAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var ctx = RequireContext();
        var artifactRef = RequireSafeIdentifier(arguments, "artifact_ref");
        var detectors = ParseOptionalDetectors(arguments, AllowedImageDetectors, ["paddle-ocr", "yunet"]);

        await RequireAnyCapabilityReadyAsync(ctx.TenantId, detectors, cancellationToken).ConfigureAwait(false);
        if (_workerClient is null || _artifactResolver is null)
            throw new ToolAuthorizationException(ErrorCodes.CapabilityUnavailable);

        var artifact = await _artifactResolver.TryResolveAsync(ctx.TenantId, ctx.PrincipalId, artifactRef, cancellationToken).ConfigureAwait(false);
        if (artifact is null || artifact.TenantId != ctx.TenantId || artifact.PrincipalId != ctx.PrincipalId)
            throw new ToolAuthorizationException(ErrorCodes.Unauthorized);

        var requestId = Guid.NewGuid().ToString("D");
        SecurityInspectionResponse response;
        try
        {
            response = await _workerClient.InspectImageAsync(
                new SecurityImageInspectionRequest(
                    requestId,
                    _options.PolicyRevision,
                    detectors,
                    artifact.Bytes,
                    artifact.ContentType,
                    artifact.Sha256Digest),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { throw new ToolAuthorizationException(ErrorCodes.CapabilityUnavailable); }

        if (response is null || response.Status != SecurityInspectionStatus.Completed)
            throw new ToolAuthorizationException(ErrorCodes.CapabilityUnavailable);

        if (!string.IsNullOrEmpty(response.ContentDigest) &&
            !string.Equals(response.ContentDigest, artifact.Sha256Digest, StringComparison.OrdinalIgnoreCase))
            throw new ToolAuthorizationException(ErrorCodes.PrivacyBlocked);

        var categoryCounts = CountFindingCategories(response.Findings);
        var regionCount = response.Findings.Count(f => f.Kind == SecurityFindingKind.ImageRegion);
        var eventId = await TryRecordEvidenceAsync(
            ctx,
            requestId,
            "image_privacy",
            "agent-security-tool",
            detectors[0],
            ResolveDetectorVersion(response, detectors[0]),
            response.Status,
            SecurityEvidenceDecision.Allow,
            SecurityEnforcementOutcome.Executed,
            regionCount > 0 ? "sensitive_regions_detected" : "clean",
            categoryCounts,
            response.DurationMs,
            cancellationToken).ConfigureAwait(false);

        return await SanitizeOutputAsync(ctx, new
        {
            mode = "inspect",
            authorization_granted = false,
            status = "completed",
            request_id = requestId,
            evidence_id = eventId.ToString("D"),
            artifact_ref = artifactRef,
            region_count = regionCount,
            category_counts = categoryCounts,
            image_dimensions = response.ImageDimensions is null
                ? null
                : new { width = response.ImageDimensions.Width, height = response.ImageDimensions.Height }
        }, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<JsonElement> HandleScanFileAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var ctx = RequireContext();
        var artifactRef = RequireSafeIdentifier(arguments, "artifact_ref");

        await RequireCapabilitiesReadyAsync(ctx.TenantId, ["malware"], cancellationToken).ConfigureAwait(false);
        if (_workerClient is null || _artifactResolver is null)
            throw new ToolAuthorizationException(ErrorCodes.CapabilityUnavailable);

        var artifact = await _artifactResolver.TryResolveAsync(ctx.TenantId, ctx.PrincipalId, artifactRef, cancellationToken).ConfigureAwait(false);
        if (artifact is null || artifact.TenantId != ctx.TenantId || artifact.PrincipalId != ctx.PrincipalId)
            throw new ToolAuthorizationException(ErrorCodes.Unauthorized);

        var requestId = Guid.NewGuid().ToString("D");
        SecurityInspectionResponse response;
        try
        {
            response = await _workerClient.InspectFileAsync(
                new SecurityFileInspectionRequest(
                    requestId,
                    _options.PolicyRevision,
                    ["malware"],
                    artifact.Bytes,
                    artifact.ContentType,
                    artifact.Sha256Digest,
                    new HashSet<string>(StringComparer.Ordinal) { "malware" }),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { throw new ToolAuthorizationException(ErrorCodes.CapabilityUnavailable); }

        EnsureCompletedResponse(response, ["malware"]);
        if (!string.IsNullOrEmpty(response.ContentDigest) &&
            !string.Equals(response.ContentDigest, artifact.Sha256Digest, StringComparison.OrdinalIgnoreCase))
            throw new ToolAuthorizationException(ErrorCodes.PrivacyBlocked);

        var malwareStatus = response.MalwareStatus ?? SecurityMalwareStatus.Unavailable;
        var detected = malwareStatus == SecurityMalwareStatus.Detected;
        var counts = detected ? new Dictionary<string, int>(StringComparer.Ordinal) { ["malware"] = 1 } : null;
        var eventId = await TryRecordEvidenceAsync(
            ctx,
            requestId,
            "malware",
            "agent-security-tool",
            "malware",
            ResolveDetectorVersion(response, "malware"),
            response.Status,
            detected ? SecurityEvidenceDecision.Block : SecurityEvidenceDecision.Allow,
            detected ? SecurityEnforcementOutcome.Prevented : SecurityEnforcementOutcome.Executed,
            detected ? ErrorCodes.MalwareDetected : "clean",
            counts,
            response.DurationMs,
            cancellationToken).ConfigureAwait(false);

        return await SanitizeOutputAsync(ctx, new
        {
            mode = "inspect",
            authorization_granted = false,
            status = "completed",
            request_id = requestId,
            evidence_id = eventId.ToString("D"),
            artifact_ref = artifactRef,
            malware_status = ToSnakeCase(malwareStatus.ToString()),
            reason_code = response.ReasonCode ?? (detected ? ErrorCodes.MalwareDetected : "clean")
        }, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<JsonElement> HandleScanPromptAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var ctx = RequireContext();
        var text = RequireBoundedText(arguments, "text");

        await RequireCapabilitiesReadyAsync(ctx.TenantId, ["deberta-injection"], cancellationToken).ConfigureAwait(false);
        if (_workerClient is null)
            throw new ToolAuthorizationException(ErrorCodes.CapabilityUnavailable);

        var requestId = Guid.NewGuid().ToString("D");
        SecurityInspectionResponse response;
        try
        {
            response = await _workerClient.InspectPromptAsync(
                new SecurityPromptInspectionRequest(
                    requestId,
                    _options.PolicyRevision,
                    ["deberta-injection"],
                    text,
                    new HashSet<string>(StringComparer.Ordinal) { "deberta-injection" }),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { throw new ToolAuthorizationException(ErrorCodes.CapabilityUnavailable); }

        EnsureCompletedResponse(response, ["deberta-injection"]);

        var maxConfidence = 0.0;
        var hits = 0;
        foreach (var finding in response.Findings)
        {
            if (finding.Kind != SecurityFindingKind.Injection)
                continue;
            if (!double.IsFinite(finding.Confidence) || finding.Confidence < 0.0 || finding.Confidence > 1.0)
                throw new ToolAuthorizationException(ErrorCodes.PrivacyBlocked);
            if (finding.Confidence > maxConfidence)
                maxConfidence = finding.Confidence;
            if (finding.Confidence >= _options.PromptInjectionThreshold)
                hits++;
        }

        var detected = hits > 0;
        var counts = detected ? new Dictionary<string, int>(StringComparer.Ordinal) { ["prompt_injection"] = hits } : null;
        var eventId = await TryRecordEvidenceAsync(
            ctx,
            requestId,
            "prompt_injection",
            "agent-security-tool",
            "deberta-injection",
            ResolveDetectorVersion(response, "deberta-injection"),
            response.Status,
            detected ? SecurityEvidenceDecision.Review : SecurityEvidenceDecision.Allow,
            SecurityEnforcementOutcome.Executed,
            detected ? ErrorCodes.PromptInjectionDetected : "clean",
            counts,
            response.DurationMs,
            cancellationToken).ConfigureAwait(false);

        return await SanitizeOutputAsync(ctx, new
        {
            mode = "inspect",
            authorization_granted = false,
            status = "completed",
            request_id = requestId,
            evidence_id = eventId.ToString("D"),
            injection_detected = detected,
            max_confidence = maxConfidence,
            threshold = _options.PromptInjectionThreshold,
            coverage = response.Detectors.Select(d => new { id = d.Id, version = d.Version, status = d.Status.ToString().ToLowerInvariant() }).ToArray()
        }, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<JsonElement> HandleRedactContentAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var ctx = RequireContext();
        var artifactRef = RequireSafeIdentifier(arguments, "artifact_ref");
        var operation = RequireSafeIdentifier(arguments, "operation");

        if (!_options.ApprovedRedactionOperations.Contains(operation))
            throw new ToolValidationException($"Redaction operation '{operation}' is not approved.");

        var requiredCaps = SecurityToolAccessPolicy.GetBackingCapabilities(SecurityToolIds.RedactContent);
        await RequireAnyCapabilityReadyAsync(ctx.TenantId, requiredCaps, cancellationToken).ConfigureAwait(false);
        if (_artifactResolver is null || _contentRedactor is null)
            throw new ToolAuthorizationException(ErrorCodes.CapabilityUnavailable);

        var artifact = await _artifactResolver.TryResolveAsync(ctx.TenantId, ctx.PrincipalId, artifactRef, cancellationToken).ConfigureAwait(false);
        if (artifact is null || artifact.TenantId != ctx.TenantId || artifact.PrincipalId != ctx.PrincipalId)
            throw new ToolAuthorizationException(ErrorCodes.Unauthorized);

        var requestId = Guid.NewGuid().ToString("D");
        SecurityInspectionResponse? inspection = null;
        if (_workerClient is not null && string.Equals(operation, "redact_image", StringComparison.Ordinal))
        {
            try
            {
                inspection = await _workerClient.InspectImageAsync(
                    new SecurityImageInspectionRequest(
                        requestId,
                        _options.PolicyRevision,
                        ["paddle-ocr", "yunet"],
                        artifact.Bytes,
                        artifact.ContentType,
                        artifact.Sha256Digest),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { throw new ToolAuthorizationException(ErrorCodes.CapabilityUnavailable); }

            if (inspection is null || inspection.Status != SecurityInspectionStatus.Completed)
                throw new ToolAuthorizationException(ErrorCodes.CapabilityUnavailable);
        }

        var redacted = await _contentRedactor.RedactAsync(
            ctx.TenantId,
            ctx.PrincipalId,
            artifact,
            operation,
            inspection,
            cancellationToken).ConfigureAwait(false);

        if (!redacted.Succeeded)
        {
            await TryRecordEvidenceAsync(
                ctx,
                requestId,
                "content_redaction",
                "agent-security-tool",
                "host-redactor",
                "1",
                SecurityInspectionStatus.Failed,
                SecurityEvidenceDecision.Block,
                SecurityEnforcementOutcome.Prevented,
                redacted.ReasonCode,
                null,
                0,
                cancellationToken).ConfigureAwait(false);
            throw new ToolAuthorizationException(ErrorCodes.PrivacyBlocked);
        }

        var stored = await _artifactResolver.StoreRedactedAsync(
            ctx.TenantId,
            ctx.PrincipalId,
            artifactRef,
            operation,
            redacted.RedactedBytes,
            redacted.ContentType,
            cancellationToken).ConfigureAwait(false);

        if (!stored.Succeeded || string.IsNullOrWhiteSpace(stored.SanitizedArtifactRef))
            throw new ToolAuthorizationException(ErrorCodes.PrivacyBlocked);

        var counts = redacted.RedactedCount > 0
            ? new Dictionary<string, int>(StringComparer.Ordinal) { ["redacted"] = redacted.RedactedCount }
            : null;
        var eventId = await TryRecordEvidenceAsync(
            ctx,
            requestId,
            "content_redaction",
            "agent-security-tool",
            "host-redactor",
            "1",
            SecurityInspectionStatus.Completed,
            SecurityEvidenceDecision.Redact,
            SecurityEnforcementOutcome.Executed,
            stored.ReasonCode,
            counts,
            0,
            cancellationToken).ConfigureAwait(false);

        return await SanitizeOutputAsync(ctx, new
        {
            mode = "enforce",
            status = "completed",
            request_id = requestId,
            evidence_id = eventId.ToString("D"),
            source_artifact_ref = artifactRef,
            sanitized_artifact_ref = stored.SanitizedArtifactRef,
            host_decision = stored.HostDecision,
            redacted_count = stored.RedactedCount,
            reason_code = stored.ReasonCode
        }, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<JsonElement> HandleCheckDestinationAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var ctx = RequireContext();
        var serviceId = RequireSafeIdentifier(arguments, "service_id");
        var operationId = RequireSafeIdentifier(arguments, "operation_id");

        await RequireCapabilitiesReadyAsync(ctx.TenantId, ["service-fencing"], cancellationToken).ConfigureAwait(false);
        if (_policyAssessmentProvider is null)
            throw new ToolAuthorizationException(ErrorCodes.CapabilityUnavailable);

        var assessment = await _policyAssessmentProvider.AssessDestinationAsync(
            ctx.TenantId, ctx.PrincipalId, serviceId, operationId, cancellationToken).ConfigureAwait(false);

        var requestId = Guid.NewGuid().ToString("D");
        var eventId = await TryRecordEvidenceAsync(
            ctx,
            requestId,
            "service_fencing",
            "agent-security-tool",
            "service-fencing",
            assessment.PolicyRevision,
            SecurityInspectionStatus.Completed,
            assessment.PermittedByPolicy ? SecurityEvidenceDecision.Allow : SecurityEvidenceDecision.Block,
            SecurityEnforcementOutcome.NotExecuted,
            assessment.ReasonCode,
            null,
            0,
            cancellationToken).ConfigureAwait(false);

        return await SanitizeOutputAsync(ctx, new
        {
            mode = "inspect",
            authorization_granted = false,
            permitted_by_policy = assessment.PermittedByPolicy,
            requires_approval = assessment.RequiresApproval,
            service_id = assessment.TargetId,
            operation_id = assessment.OperationId,
            policy_revision = assessment.PolicyRevision,
            reason_code = assessment.ReasonCode,
            evidence_id = eventId.ToString("D")
        }, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<JsonElement> HandleCheckDataAccessAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var ctx = RequireContext();
        var dataOperationId = RequireSafeIdentifier(arguments, "data_operation_id");

        await RequireCapabilitiesReadyAsync(ctx.TenantId, ["database-fencing"], cancellationToken).ConfigureAwait(false);
        if (_policyAssessmentProvider is null)
            throw new ToolAuthorizationException(ErrorCodes.CapabilityUnavailable);

        var assessment = await _policyAssessmentProvider.AssessDataAccessAsync(
            ctx.TenantId, ctx.PrincipalId, dataOperationId, cancellationToken).ConfigureAwait(false);

        var requestId = Guid.NewGuid().ToString("D");
        var eventId = await TryRecordEvidenceAsync(
            ctx,
            requestId,
            "database_fencing",
            "agent-security-tool",
            "database-fencing",
            assessment.PolicyRevision,
            SecurityInspectionStatus.Completed,
            assessment.PermittedByPolicy ? SecurityEvidenceDecision.Allow : SecurityEvidenceDecision.Block,
            SecurityEnforcementOutcome.NotExecuted,
            assessment.ReasonCode,
            null,
            0,
            cancellationToken).ConfigureAwait(false);

        return await SanitizeOutputAsync(ctx, new
        {
            mode = "inspect",
            authorization_granted = false,
            permitted_by_policy = assessment.PermittedByPolicy,
            requires_approval = assessment.RequiresApproval,
            data_operation_id = assessment.OperationId,
            policy_revision = assessment.PolicyRevision,
            reason_code = assessment.ReasonCode,
            evidence_id = eventId.ToString("D")
        }, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<JsonElement> HandleExplainEventAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var ctx = RequireContext();
        var rawEvidenceId = arguments.GetProperty("evidence_id").GetString();
        if (string.IsNullOrWhiteSpace(rawEvidenceId) || !Guid.TryParse(rawEvidenceId, out var eventId) || eventId == Guid.Empty)
            throw new ToolValidationException("Argument 'evidence_id' must be a valid non-empty UUID.");

        await RequireCapabilitiesReadyAsync(ctx.TenantId, ["security-evidence"], cancellationToken).ConfigureAwait(false);
        if (_evidenceLookup is null)
            throw new ToolAuthorizationException(ErrorCodes.CapabilityUnavailable);

        var appId = string.IsNullOrWhiteSpace(_options.ApplicationId) ? ctx.ApplicationId : _options.ApplicationId;
        var ev = await _evidenceLookup.GetByIdAsync(ctx.TenantId, appId, eventId, cancellationToken).ConfigureAwait(false);
        if (ev is null || !string.Equals(ev.TenantId, ctx.TenantId.Value.ToString(), StringComparison.Ordinal))
            throw new ToolAuthorizationException(ErrorCodes.Unauthorized);

        return await SanitizeOutputAsync(ctx, new
        {
            mode = "inspect",
            event_id = ev.EventId.ToString("D"),
            request_id = ev.RequestId,
            occurred_at = ev.OccurredAt.ToUniversalTime().ToString("O"),
            capability = ev.Capability,
            boundary = ev.Boundary,
            detector_id = ev.DetectorId,
            detector_version = ev.DetectorVersion,
            policy_revision = ev.PolicyRevision,
            inspection_status = ToSnakeCase(ev.InspectionStatus.ToString()),
            decision = ToSnakeCase(ev.Decision.ToString()),
            enforcement_outcome = ToSnakeCase(ev.EnforcementOutcome.ToString()),
            reason_code = ev.ReasonCode,
            category_counts = ev.CategoryCounts,
            duration_ms = ev.DurationMs,
            integrity_digest = ev.IntegrityDigest
        }, cancellationToken).ConfigureAwait(false);
    }

    private static SecurityToolExecutionContext RequireContext() =>
        SecurityToolExecutionContextScope.Current ?? throw new ToolAuthorizationException(ErrorCodes.Unauthorized);

    private string RequireBoundedText(JsonElement arguments, string propertyName)
    {
        if (!arguments.TryGetProperty(propertyName, out var prop) || prop.ValueKind != JsonValueKind.String)
            throw new ToolValidationException($"Required argument '{propertyName}' must be a string.");
        var value = prop.GetString() ?? string.Empty;
        if (Encoding.UTF8.GetByteCount(value) > _options.MaxTextBytes)
            throw new ToolValidationException($"Argument '{propertyName}' exceeds maximum allowed byte length of {_options.MaxTextBytes}.");
        return value;
    }

    private static string RequireSafeIdentifier(JsonElement arguments, string propertyName)
    {
        if (!arguments.TryGetProperty(propertyName, out var prop) || prop.ValueKind != JsonValueKind.String)
            throw new ToolValidationException($"Required argument '{propertyName}' must be a string.");
        var value = prop.GetString();
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
            throw new ToolValidationException($"Argument '{propertyName}' must be a non-empty identifier up to 128 characters.");
        if (value.StartsWith('.') || value.Contains("..", StringComparison.Ordinal) || value.Contains("://", StringComparison.Ordinal))
            throw new ToolValidationException($"Argument '{propertyName}' contains disallowed path or URL syntax.");

        foreach (var ch in value)
        {
            var allowed = ch is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '_' or '-' or ':';
            if (!allowed)
                throw new ToolValidationException($"Argument '{propertyName}' contains disallowed characters.");
        }

        return value;
    }

    private static string[] ParseOptionalDetectors(JsonElement arguments, IReadOnlySet<string> allowed, string[] defaultDetectors)
    {
        if (!arguments.TryGetProperty("detectors", out var prop) || prop.ValueKind == JsonValueKind.Null)
            return defaultDetectors;
        if (prop.ValueKind != JsonValueKind.Array)
            throw new ToolValidationException("Argument 'detectors' must be an array of strings.");

        var list = new List<string>();
        foreach (var item in prop.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new ToolValidationException("Each detector entry must be a string.");
            var id = item.GetString();
            if (string.IsNullOrWhiteSpace(id) || !allowed.Contains(id))
                throw new ToolValidationException($"Detector '{id}' is not allowed for this tool.");
            if (!list.Contains(id, StringComparer.Ordinal))
                list.Add(id);
        }

        if (list.Count == 0)
            throw new ToolValidationException("At least one detector must be specified.");
        return list.ToArray();
    }

    private async ValueTask RequireCapabilitiesReadyAsync(TenantId tenantId, IReadOnlyList<string> capabilityIds, CancellationToken cancellationToken)
    {
        var snapshot = await GetValidSnapshotAsync(cancellationToken).ConfigureAwait(false);
        foreach (var capId in capabilityIds)
        {
            if (_options.IsCapabilityEnabledForTenant is null || !_options.IsCapabilityEnabledForTenant(tenantId, capId))
                throw new ToolAuthorizationException(ErrorCodes.ToolNotAllowed);
            var cap = snapshot.Capabilities.FirstOrDefault(c => string.Equals(c.Id, capId, StringComparison.Ordinal));
            if (cap is null || cap.State != SecurityCapabilityState.Ready)
                throw new ToolAuthorizationException(ErrorCodes.CapabilityUnavailable);
        }
    }

    private async ValueTask RequireAnyCapabilityReadyAsync(TenantId tenantId, IReadOnlyList<string> capabilityIds, CancellationToken cancellationToken)
    {
        var snapshot = await GetValidSnapshotAsync(cancellationToken).ConfigureAwait(false);
        foreach (var capId in capabilityIds)
        {
            if (_options.IsCapabilityEnabledForTenant is null || !_options.IsCapabilityEnabledForTenant(tenantId, capId))
                continue;
            var cap = snapshot.Capabilities.FirstOrDefault(c => string.Equals(c.Id, capId, StringComparison.Ordinal));
            if (cap is not null && cap.State == SecurityCapabilityState.Ready)
                return;
        }

        throw new ToolAuthorizationException(ErrorCodes.CapabilityUnavailable);
    }

    private async ValueTask<SecurityCapabilitiesSnapshot> GetValidSnapshotAsync(CancellationToken cancellationToken)
    {
        SecurityCapabilitiesSnapshot snapshot;
        try
        {
            snapshot = await _healthProvider.GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { throw new ToolAuthorizationException(ErrorCodes.CapabilityUnavailable); }

        if (snapshot is null || snapshot.ExpiresAt <= _timeProvider.GetUtcNow() || snapshot.Capabilities is null)
            throw new ToolAuthorizationException(ErrorCodes.CapabilityUnavailable);
        return snapshot;
    }

    private static void EnsureCompletedResponse(SecurityInspectionResponse? response, IReadOnlyList<string> requiredDetectors)
    {
        if (response is null || response.Status != SecurityInspectionStatus.Completed)
            throw new ToolAuthorizationException(ErrorCodes.CapabilityUnavailable);
        foreach (var req in requiredDetectors)
        {
            var det = response.Detectors.FirstOrDefault(d => string.Equals(d.Id, req, StringComparison.Ordinal));
            if (det is null || det.Status != SecurityInspectionStatus.Completed)
                throw new ToolAuthorizationException(ErrorCodes.CapabilityUnavailable);
        }
    }

    private static string ResolveDetectorVersion(SecurityInspectionResponse response, string detectorId)
    {
        var version = response.Detectors.FirstOrDefault(d => string.Equals(d.Id, detectorId, StringComparison.Ordinal))?.Version;
        return SanitizeEvidenceId(version, "unknown", 128);
    }

    private static Dictionary<string, int> CountFindingCategories(IReadOnlyList<SecurityFinding> findings)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var finding in findings)
        {
            var category = SanitizeEvidenceId(finding.Category, "unknown", 64);
            counts[category] = counts.TryGetValue(category, out var current) ? current + 1 : 1;
        }
        return counts;
    }

    private async ValueTask<JsonElement> SanitizeOutputAsync(SecurityToolExecutionContext ctx, object payload, CancellationToken cancellationToken)
    {
        var rawJson = JsonSerializer.Serialize(payload);
        if (_outputSanitizer is not null)
        {
            try
            {
                rawJson = await _outputSanitizer.SanitizeAsync(ctx.TenantId, ctx.PrincipalId, rawJson, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { throw new ToolAuthorizationException(ErrorCodes.PrivacyBlocked); }
        }

        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new ToolAuthorizationException(ErrorCodes.PrivacyBlocked);
        }
    }

    private async ValueTask<Guid> TryRecordEvidenceAsync(
        SecurityToolExecutionContext ctx,
        string requestId,
        string capability,
        string boundary,
        string detectorId,
        string detectorVersion,
        SecurityInspectionStatus inspectionStatus,
        SecurityEvidenceDecision decision,
        SecurityEnforcementOutcome enforcementOutcome,
        string reasonCode,
        IReadOnlyDictionary<string, int>? categoryCounts,
        long durationMs,
        CancellationToken cancellationToken)
    {
        var eventId = Guid.NewGuid();
        if (_evidenceSink is null)
            return eventId;

        try
        {
            var appId = string.IsNullOrWhiteSpace(_options.ApplicationId) ? ctx.ApplicationId : _options.ApplicationId;
            var ev = SecurityEvidenceEvent.Create(
                eventId: eventId,
                requestId: SanitizeEvidenceId(requestId, eventId.ToString("D"), 128),
                tenantId: SanitizeEvidenceId(ctx.TenantId.Value.ToString(), "unknown-tenant", 128),
                applicationId: SanitizeEvidenceId(appId, "openconnect-agent", 128),
                occurredAt: _timeProvider.GetUtcNow(),
                capability: SanitizeEvidenceId(capability, "security_tool", 64),
                boundary: SanitizeEvidenceId(boundary, "agent-security-tool", 64),
                detectorId: SanitizeEvidenceId(detectorId, "security-tool", 64),
                detectorVersion: SanitizeEvidenceId(detectorVersion, "unknown", 128),
                policyRevision: SanitizeEvidenceId(_options.PolicyRevision, "1", 64),
                inspectionStatus: inspectionStatus,
                decision: decision,
                enforcementOutcome: enforcementOutcome,
                reasonCode: SanitizeEvidenceId(reasonCode, "unknown", 64),
                categoryCounts: categoryCounts,
                durationMs: Math.Max(0, durationMs));
            await _evidenceSink.RecordAsync(ev, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            // Evidence sink failure must never bypass enforcement or break tool execution.
        }

        return eventId;
    }

    private static string SanitizeEvidenceId(string? value, string fallback, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;
        var sb = new StringBuilder(Math.Min(value.Length, maxLength));
        foreach (var ch in value)
        {
            if (sb.Length >= maxLength)
                break;
            var allowed = ch is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '_' or '-' or ':';
            sb.Append(allowed ? ch : '_');
        }
        return sb.Length > 0 ? sb.ToString() : fallback;
    }

    private static string ToSnakeCase(string name)
    {
        var result = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c) && i > 0 && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1])))
                result.Append('_');
            result.Append(char.ToLowerInvariant(c));
        }
        return result.ToString();
    }
}
