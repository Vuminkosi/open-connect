using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenConnect.Abstractions;

namespace OpenConnect.Privacy.Worker;

/// <summary>Configuration for <see cref="HttpSecurityWorkerClient"/>.</summary>
public sealed class SecurityWorkerClientOptions
{
    /// <summary>Trusted base endpoint of the private Security Worker.</summary>
    public Uri? Endpoint { get; set; }
    /// <summary>Whether bearer authentication is required for worker requests. Defaults to <see langword="true"/>.</summary>
    public bool RequireAuthenticatedTransport { get; set; } = true;
    /// <summary>Explicit opt-in allowing HTTP loopback endpoints in non-production development.</summary>
    public bool AllowInsecureLoopbackHttpInDevelopment { get; set; } = false;
    /// <summary>Host-held bearer token supplied from a secret store; never sourced from model or user input.</summary>
    public string? BearerToken { get; set; }
    /// <summary>Maximum UTF-8 byte length for text/prompt inspection requests (default 64 KiB).</summary>
    public int MaxTextBytes { get; set; } = 64 * 1024;
    /// <summary>Maximum byte length for image inspection requests (default 10 MiB).</summary>
    public int MaxImageBytes { get; set; } = 10 * 1024 * 1024;
    /// <summary>Maximum byte length for file inspection requests (default 20 MiB).</summary>
    public int MaxFileBytes { get; set; } = 20 * 1024 * 1024;
    /// <summary>Maximum response body byte length accepted from the worker (default 2 MiB).</summary>
    public int MaxResponseBytes { get; set; } = 2 * 1024 * 1024;
    /// <summary>Maximum concurrent in-flight worker requests.</summary>
    public int MaxConcurrentRequests { get; set; } = 2;
    /// <summary>Per-request timeout.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(15);
    /// <summary>Lease TTL applied to verified capability snapshots.</summary>
    public TimeSpan CapabilityLeaseDuration { get; set; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// Typed, authenticated HTTP client for the private OpenConnect Security Worker.
/// Enforces no redirects, bounded request/response sizes, concurrency limits, timeouts,
/// trusted endpoint validation, and fail-closed mapping on any transport/auth/schema/coverage failure.
/// </summary>
public sealed class HttpSecurityWorkerClient : ISecurityWorkerClient, IDisposable
{
    private static readonly IReadOnlySet<string> KnownDetectors = new HashSet<string>(StringComparer.Ordinal)
    {
        "rules", "gliner-pii", "deberta-injection", "qwen3guard", "paddle-ocr", "yunet", "malware"
    };

    private readonly SecurityWorkerClientOptions _options;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly SemaphoreSlim _concurrencyGate;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a new <see cref="HttpSecurityWorkerClient"/> using hardened transport settings.</summary>
    public HttpSecurityWorkerClient(
        SecurityWorkerClientOptions options,
        HttpMessageHandler? messageHandler = null,
        TimeProvider? timeProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ValidateOptions(options);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _concurrencyGate = new SemaphoreSlim(options.MaxConcurrentRequests, options.MaxConcurrentRequests);

        var handler = messageHandler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        };
        _ownsHttpClient = messageHandler is null;
        _httpClient = new HttpClient(handler, disposeHandler: _ownsHttpClient)
        {
            BaseAddress = options.Endpoint!,
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    /// <inheritdoc />
    public async ValueTask<SecurityCapabilitiesSnapshot> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        using var request = CreateRequest(HttpMethod.Get, "/internal/v1/capabilities");
        var (ok, json, _) = await SendAndReadBoundedJsonAsync(request, cancellationToken).ConfigureAwait(false);
        if (!ok || json is null)
        {
            return new SecurityCapabilitiesSnapshot(now, now, Array.Empty<SecurityCapabilityStatus>());
        }

        using (json)
        {
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("capabilities", out var capsProp) ||
                capsProp.ValueKind != JsonValueKind.Array ||
                capsProp.GetArrayLength() > 64)
            {
                return new SecurityCapabilitiesSnapshot(now, now, Array.Empty<SecurityCapabilityStatus>());
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var capabilities = new List<SecurityCapabilityStatus>();
            foreach (var item in capsProp.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    return new SecurityCapabilitiesSnapshot(now, now, Array.Empty<SecurityCapabilityStatus>());

                if (!TryGetString(item, "schemaVersion", 64, out var schemaVersion) || schemaVersion != "openconnect.v1" ||
                    !TryGetString(item, "id", 256, out var id) || !seen.Add(id) ||
                    !TryGetString(item, "kind", 32, out var kind) ||
                    !TryGetString(item, "state", 32, out var stateStr) ||
                    !TryGetString(item, "version", 128, out var version) ||
                    !item.TryGetProperty("localOnly", out var localOnlyProp) ||
                    localOnlyProp.ValueKind != JsonValueKind.True)
                {
                    return new SecurityCapabilitiesSnapshot(now, now, Array.Empty<SecurityCapabilityStatus>());
                }

                var state = ParseCapabilityState(stateStr);
                if (!state.HasValue)
                    return new SecurityCapabilitiesSnapshot(now, now, Array.Empty<SecurityCapabilityStatus>());

                capabilities.Add(new SecurityCapabilityStatus(
                    id,
                    kind,
                    state.Value,
                    version,
                    LocalOnly: true,
                    CheckedAt: now));
            }

            return new SecurityCapabilitiesSnapshot(now, now.Add(_options.CapabilityLeaseDuration), capabilities);
        }
    }

    /// <inheritdoc />
    public async ValueTask<SecurityInspectionResponse> InspectTextAsync(SecurityTextInspectionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ValidateCommonRequest(request.RequestId, request.PolicyRevision, request.Detectors, out var errorReason))
            return FailClosed(request.RequestId, SecurityInspectionStatus.Failed, errorReason);

        var textBytes = Encoding.UTF8.GetByteCount(request.Text ?? string.Empty);
        if (textBytes > _options.MaxTextBytes)
            return FailClosed(request.RequestId, SecurityInspectionStatus.LimitExceeded, "text_limit_exceeded");

        var payload = SerializeTextPayload(request.RequestId, request.PolicyRevision, "text", request.Detectors, request.Text ?? string.Empty);
        using var httpRequest = CreateRequest(HttpMethod.Post, "/internal/v1/inspect/text");
        httpRequest.Content = new ByteArrayContent(payload);
        httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        return await ExecuteInspectionAsync(
            httpRequest,
            request.RequestId,
            expectedModality: "text",
            requestedDetectors: request.Detectors,
            requiredDetectors: request.RequiredDetectors,
            expectedSha256: null,
            sourceText: request.Text ?? string.Empty,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<SecurityInspectionResponse> InspectPromptAsync(SecurityPromptInspectionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ValidateCommonRequest(request.RequestId, request.PolicyRevision, request.Detectors, out var errorReason))
            return FailClosed(request.RequestId, SecurityInspectionStatus.Failed, errorReason);

        var promptBytes = Encoding.UTF8.GetByteCount(request.Prompt ?? string.Empty);
        if (promptBytes > _options.MaxTextBytes)
            return FailClosed(request.RequestId, SecurityInspectionStatus.LimitExceeded, "prompt_limit_exceeded");

        var payload = SerializeTextPayload(request.RequestId, request.PolicyRevision, "prompt", request.Detectors, request.Prompt ?? string.Empty);
        using var httpRequest = CreateRequest(HttpMethod.Post, "/internal/v1/inspect/prompt");
        httpRequest.Content = new ByteArrayContent(payload);
        httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        return await ExecuteInspectionAsync(
            httpRequest,
            request.RequestId,
            expectedModality: "prompt",
            requestedDetectors: request.Detectors,
            requiredDetectors: request.RequiredDetectors,
            expectedSha256: null,
            sourceText: request.Prompt ?? string.Empty,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<SecurityInspectionResponse> InspectImageAsync(SecurityImageInspectionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ValidateCommonRequest(request.RequestId, request.PolicyRevision, request.Detectors, out var errorReason))
            return FailClosed(request.RequestId, SecurityInspectionStatus.Failed, errorReason);
        if (request.ImageBytes.IsEmpty || request.ImageBytes.Length > _options.MaxImageBytes)
            return FailClosed(request.RequestId, SecurityInspectionStatus.LimitExceeded, "image_limit_exceeded");
        if (string.IsNullOrWhiteSpace(request.ContentType) || !IsValidSha256Hex(request.Sha256Digest))
            return FailClosed(request.RequestId, SecurityInspectionStatus.Failed, "invalid_request");

        var actualDigest = ComputeSha256Hex(request.ImageBytes.Span);
        if (!string.Equals(actualDigest, request.Sha256Digest, StringComparison.OrdinalIgnoreCase))
            return FailClosed(request.RequestId, SecurityInspectionStatus.Failed, "digest_mismatch");

        using var multipart = BuildBinaryMultipart(request.RequestId, request.PolicyRevision, "image", request.Detectors, request.ImageBytes, request.ContentType, actualDigest);
        using var httpRequest = CreateRequest(HttpMethod.Post, "/internal/v1/inspect/image");
        httpRequest.Content = multipart;

        return await ExecuteInspectionAsync(
            httpRequest,
            request.RequestId,
            expectedModality: "image",
            requestedDetectors: request.Detectors,
            requiredDetectors: request.RequiredDetectors,
            expectedSha256: actualDigest,
            sourceText: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<SecurityInspectionResponse> InspectFileAsync(SecurityFileInspectionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ValidateCommonRequest(request.RequestId, request.PolicyRevision, request.Detectors, out var errorReason))
            return FailClosed(request.RequestId, SecurityInspectionStatus.Failed, errorReason);
        if (request.FileBytes.IsEmpty || request.FileBytes.Length > _options.MaxFileBytes)
            return FailClosed(request.RequestId, SecurityInspectionStatus.LimitExceeded, "file_limit_exceeded");
        if (string.IsNullOrWhiteSpace(request.ContentType) || !IsValidSha256Hex(request.Sha256Digest))
            return FailClosed(request.RequestId, SecurityInspectionStatus.Failed, "invalid_request");

        var actualDigest = ComputeSha256Hex(request.FileBytes.Span);
        if (!string.Equals(actualDigest, request.Sha256Digest, StringComparison.OrdinalIgnoreCase))
            return FailClosed(request.RequestId, SecurityInspectionStatus.Failed, "digest_mismatch");

        using var multipart = BuildBinaryMultipart(request.RequestId, request.PolicyRevision, "file", request.Detectors, request.FileBytes, request.ContentType, actualDigest);
        using var httpRequest = CreateRequest(HttpMethod.Post, "/internal/v1/inspect/file");
        httpRequest.Content = multipart;

        return await ExecuteInspectionAsync(
            httpRequest,
            request.RequestId,
            expectedModality: "file",
            requestedDetectors: request.Detectors,
            requiredDetectors: request.RequiredDetectors,
            expectedSha256: actualDigest,
            sourceText: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _concurrencyGate.Dispose();
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }

    private async ValueTask<SecurityInspectionResponse> ExecuteInspectionAsync(
        HttpRequestMessage httpRequest,
        string requestId,
        string expectedModality,
        IReadOnlyList<string> requestedDetectors,
        IReadOnlySet<string>? requiredDetectors,
        string? expectedSha256,
        string? sourceText,
        CancellationToken cancellationToken)
    {
        var (ok, json, failReason) = await SendAndReadBoundedJsonAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        if (!ok || json is null)
            return FailClosed(requestId, SecurityInspectionStatus.Unavailable, failReason);

        using (json)
        {
            return ParseAndValidateResponse(
                json.RootElement,
                requestId,
                expectedModality,
                requestedDetectors,
                requiredDetectors,
                expectedSha256,
                sourceText);
        }
    }

    private async Task<(bool Ok, JsonDocument? Document, string ReasonCode)> SendAndReadBoundedJsonAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        await _concurrencyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_options.RequestTimeout);

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);
            var statusCode = (int)response.StatusCode;
            if (statusCode is >= 300 and <= 399)
                return (false, null, "redirect_not_allowed");
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return (false, null, "worker_auth_failed");
            if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
                return (false, null, "worker_unavailable");
            if (!response.IsSuccessStatusCode)
                return (false, null, "worker_http_error");

            if (response.Content.Headers.ContentLength is long cl && cl > _options.MaxResponseBytes)
                return (false, null, "response_limit_exceeded");

            await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            long totalRead = 0;
            while (true)
            {
                var read = await stream.ReadAsync(chunk, timeoutCts.Token).ConfigureAwait(false);
                if (read == 0)
                    break;
                totalRead += read;
                if (totalRead > _options.MaxResponseBytes)
                    return (false, null, "response_limit_exceeded");
                buffer.Write(chunk, 0, read);
            }

            buffer.Position = 0;
            var doc = await JsonDocument.ParseAsync(buffer, new JsonDocumentOptions { MaxDepth = 32 }, timeoutCts.Token).ConfigureAwait(false);
            return (true, doc, "ok");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return (false, null, "worker_timeout");
        }
        catch (JsonException)
        {
            return (false, null, "malformed_worker_response");
        }
        catch (HttpRequestException)
        {
            return (false, null, "worker_transport_error");
        }
        finally
        {
            _concurrencyGate.Release();
        }
    }

    private static SecurityInspectionResponse ParseAndValidateResponse(
        JsonElement root,
        string expectedRequestId,
        string expectedModality,
        IReadOnlyList<string> requestedDetectors,
        IReadOnlySet<string>? requiredDetectors,
        string? expectedSha256,
        string? sourceText)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return FailClosed(expectedRequestId, SecurityInspectionStatus.Failed, "malformed_worker_response");

        if (!TryGetString(root, "schemaVersion", 64, out var schemaVersion) ||
            schemaVersion != SecurityContractVersions.InspectionV1)
            return FailClosed(expectedRequestId, SecurityInspectionStatus.Failed, "incompatible_schema_version");

        if (!TryGetString(root, "requestId", 128, out var responseRequestId) ||
            !string.Equals(responseRequestId, expectedRequestId, StringComparison.Ordinal))
            return FailClosed(expectedRequestId, SecurityInspectionStatus.Failed, "request_id_mismatch");

        if (root.TryGetProperty("modality", out var modalityProp) &&
            modalityProp.ValueKind == JsonValueKind.String &&
            !string.Equals(modalityProp.GetString(), expectedModality, StringComparison.Ordinal))
            return FailClosed(expectedRequestId, SecurityInspectionStatus.Failed, "modality_mismatch");

        if (!TryGetString(root, "status", 32, out var statusStr) || !TryParseInspectionStatus(statusStr, out var overallStatus))
            return FailClosed(expectedRequestId, SecurityInspectionStatus.Failed, "unknown_status");

        var requestedSet = new HashSet<string>(requestedDetectors, StringComparer.Ordinal);
        var requiredSet = requiredDetectors ?? requestedSet;

        if (!root.TryGetProperty("detectors", out var detectorsProp) ||
            detectorsProp.ValueKind != JsonValueKind.Array ||
            detectorsProp.GetArrayLength() > 32)
            return FailClosed(expectedRequestId, SecurityInspectionStatus.Failed, "malformed_detectors");

        var seenDetectors = new HashSet<string>(StringComparer.Ordinal);
        var detectorStatuses = new List<SecurityDetectorStatus>();
        foreach (var detEl in detectorsProp.EnumerateArray())
        {
            if (detEl.ValueKind != JsonValueKind.Object ||
                !TryGetString(detEl, "id", 64, out var detId) ||
                !KnownDetectors.Contains(detId) ||
                !seenDetectors.Add(detId) ||
                !TryGetString(detEl, "version", 128, out var detVersion) ||
                !TryGetString(detEl, "status", 32, out var detStatusStr) ||
                !TryParseInspectionStatus(detStatusStr, out var detStatus))
            {
                return FailClosed(expectedRequestId, SecurityInspectionStatus.Failed, "malformed_detector_status");
            }

            string? detReason = null;
            if (detEl.TryGetProperty("reasonCode", out var rProp) && rProp.ValueKind == JsonValueKind.String)
                detReason = rProp.GetString();

            detectorStatuses.Add(new SecurityDetectorStatus(detId, detVersion, detStatus, detReason));
        }

        // Verify required coverage: every required detector must be present and completed.
        foreach (var req in requiredSet)
        {
            var match = detectorStatuses.FirstOrDefault(d => string.Equals(d.Id, req, StringComparison.Ordinal));
            if (match is null || match.Status != SecurityInspectionStatus.Completed)
                return FailClosed(expectedRequestId, SecurityInspectionStatus.Unavailable, "required_coverage_missing");
        }

        string? offsetUnit = null;
        if (root.TryGetProperty("offsetUnit", out var ouProp) && ouProp.ValueKind == JsonValueKind.String)
            offsetUnit = ouProp.GetString();
        if (expectedModality == "text" && offsetUnit is not null && offsetUnit != SecurityContractVersions.OffsetUnitUnicodeScalar)
            return FailClosed(expectedRequestId, SecurityInspectionStatus.Failed, "unsupported_offset_unit");

        string? contentSha256 = null;
        if (root.TryGetProperty("contentSha256", out var digestProp) && digestProp.ValueKind == JsonValueKind.String)
            contentSha256 = digestProp.GetString();
        if (expectedSha256 is not null &&
            !string.Equals(contentSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            return FailClosed(expectedRequestId, SecurityInspectionStatus.Failed, "digest_mismatch");

        SecurityImageDimensions? imageDimensions = null;
        if (root.TryGetProperty("imageMetadata", out var imgMeta) && imgMeta.ValueKind == JsonValueKind.Object)
        {
            if (!imgMeta.TryGetProperty("width", out var wProp) || !wProp.TryGetInt32(out var width) || width <= 0 ||
                !imgMeta.TryGetProperty("height", out var hProp) || !hProp.TryGetInt32(out var height) || height <= 0)
                return FailClosed(expectedRequestId, SecurityInspectionStatus.Failed, "invalid_image_metadata");
            var coordSpace = imgMeta.TryGetProperty("coordinateSpace", out var csProp) && csProp.ValueKind == JsonValueKind.String
                ? csProp.GetString() ?? "top-left-pixels"
                : "top-left-pixels";
            var oriented = !imgMeta.TryGetProperty("orientationNormalized", out var onProp) || onProp.ValueKind == JsonValueKind.True;
            imageDimensions = new SecurityImageDimensions(width, height, coordSpace, oriented);
        }

        long durationMs = 0;
        if (root.TryGetProperty("durationMs", out var durProp) && durProp.ValueKind == JsonValueKind.Number)
        {
            if (!durProp.TryGetInt64(out durationMs) || durationMs < 0)
                return FailClosed(expectedRequestId, SecurityInspectionStatus.Failed, "invalid_duration");
        }

        if (!root.TryGetProperty("findings", out var findingsProp) ||
            findingsProp.ValueKind != JsonValueKind.Array ||
            findingsProp.GetArrayLength() > 20_000)
            return FailClosed(expectedRequestId, SecurityInspectionStatus.Failed, "malformed_findings");

        var findings = new List<SecurityFinding>();
        SecurityMalwareStatus? malwareStatus = null;
        foreach (var fEl in findingsProp.EnumerateArray())
        {
            if (!TryParseFinding(fEl, expectedModality, seenDetectors, out var finding))
                return FailClosed(expectedRequestId, SecurityInspectionStatus.Failed, "invalid_finding");
            if (finding.MalwareStatus.HasValue)
                malwareStatus = finding.MalwareStatus;
            findings.Add(finding);
        }

        if (expectedModality == "text" && sourceText is not null)
        {
            if (!UnicodeScalarOffsetConverter.TryValidateTextFindings(sourceText, findings, out _))
                return FailClosed(expectedRequestId, SecurityInspectionStatus.Failed, "invalid_text_offsets");
        }
        else if (expectedModality == "image")
        {
            if (!UnicodeScalarOffsetConverter.ValidateImageFindings(imageDimensions, findings))
                return FailClosed(expectedRequestId, SecurityInspectionStatus.Failed, "invalid_image_regions");
        }

        return new SecurityInspectionResponse(
            schemaVersion,
            responseRequestId,
            overallStatus,
            detectorStatuses,
            findings,
            offsetUnit,
            durationMs,
            contentSha256,
            imageDimensions,
            malwareStatus,
            overallStatus == SecurityInspectionStatus.Completed ? "ok" : statusStr);
    }

    private static bool TryParseFinding(
        JsonElement el,
        string expectedModality,
        HashSet<string> reportedDetectors,
        out SecurityFinding finding)
    {
        finding = null!;
        if (el.ValueKind != JsonValueKind.Object)
            return false;
        if (!TryGetString(el, "detectorId", 64, out var detectorId) || !reportedDetectors.Contains(detectorId))
            return false;

        // Kind defaults based on modality if omitted in simplified payloads, otherwise must be valid.
        SecurityFindingKind kind;
        if (TryGetString(el, "kind", 32, out var kindStr))
        {
            if (!TryParseFindingKind(kindStr, out kind))
                return false;
        }
        else
        {
            kind = expectedModality switch
            {
                "text" => SecurityFindingKind.TextSpan,
                "image" => SecurityFindingKind.ImageRegion,
                "file" => SecurityFindingKind.Malware,
                "prompt" => SecurityFindingKind.Injection,
                _ => (SecurityFindingKind)(-1)
            };
            if (!Enum.IsDefined(kind))
                return false;
        }

        SecurityPolicyAction? recommendedAction = null;
        if (TryGetString(el, "recommendedAction", 32, out var actionStr))
        {
            if (!TryParsePolicyAction(actionStr, out var parsedAction))
                return false;
            recommendedAction = parsedAction;
        }

        if (kind == SecurityFindingKind.TextSpan)
        {
            if (!TryGetString(el, "category", 64, out var category) ||
                !el.TryGetProperty("start", out var sProp) || !sProp.TryGetInt32(out var start) ||
                !el.TryGetProperty("end", out var eProp) || !eProp.TryGetInt32(out var end) ||
                !TryGetUnitDouble(el, "confidence", out var confidence))
                return false;
            finding = new SecurityFinding(kind, detectorId, category, confidence, Start: start, End: end, RecommendedAction: recommendedAction);
            return true;
        }

        if (kind == SecurityFindingKind.ImageRegion)
        {
            if (!TryGetString(el, "category", 64, out var category) ||
                !el.TryGetProperty("x", out var xProp) || !xProp.TryGetInt32(out var x) ||
                !el.TryGetProperty("y", out var yProp) || !yProp.TryGetInt32(out var y) ||
                !el.TryGetProperty("width", out var wProp) || !wProp.TryGetInt32(out var w) ||
                !el.TryGetProperty("height", out var hProp) || !hProp.TryGetInt32(out var h) ||
                !TryGetUnitDouble(el, "confidence", out var confidence))
                return false;
            finding = new SecurityFinding(kind, detectorId, category, confidence, Region: new SecurityImageRegion(x, y, w, h), RecommendedAction: recommendedAction);
            return true;
        }

        if (kind == SecurityFindingKind.Malware)
        {
            if (!TryGetString(el, "outcome", 32, out var outcomeStr) ||
                !TryParseMalwareStatus(outcomeStr, out var malwareStatus))
                return false;
            string? reasonCode = el.TryGetProperty("reasonCode", out var rcProp) && rcProp.ValueKind == JsonValueKind.String ? rcProp.GetString() : null;
            string? sigVersion = el.TryGetProperty("signatureVersion", out var svProp) && svProp.ValueKind == JsonValueKind.String ? svProp.GetString() : null;
            finding = new SecurityFinding(kind, detectorId, "MALWARE", 1.0, MalwareStatus: malwareStatus, ReasonCode: reasonCode, SignatureVersion: sigVersion);
            return true;
        }

        if (kind == SecurityFindingKind.Injection)
        {
            if (!TryGetString(el, "label", 32, out var label) ||
                label is not ("benign" or "injection" or "uncertain") ||
                !TryGetUnitDouble(el, "score", out var score))
                return false;
            double? threshold = null;
            if (el.TryGetProperty("threshold", out var tProp) && tProp.ValueKind == JsonValueKind.Number)
            {
                if (!tProp.TryGetDouble(out var tVal) || !double.IsFinite(tVal) || tVal is < 0 or > 1)
                    return false;
                threshold = tVal;
            }
            string? modelRev = el.TryGetProperty("modelRevision", out var mrProp) && mrProp.ValueKind == JsonValueKind.String ? mrProp.GetString() : null;
            finding = new SecurityFinding(kind, detectorId, "PROMPT_INJECTION", score, Label: label, Threshold: threshold, ModelRevision: modelRev, RecommendedAction: recommendedAction);
            return true;
        }

        return false;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string relativePath)
    {
        var req = new HttpRequestMessage(method, relativePath);
        if (!string.IsNullOrWhiteSpace(_options.BearerToken))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.BearerToken);
        return req;
    }

    private static byte[] SerializeTextPayload(string requestId, string policyRevision, string modality, IReadOnlyList<string> detectors, string text)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteString("schemaVersion", SecurityContractVersions.InspectionV1);
            writer.WriteString("requestId", requestId);
            writer.WriteString("policyRevision", policyRevision);
            writer.WriteString("modality", modality);
            writer.WritePropertyName("detectors");
            writer.WriteStartArray();
            foreach (var d in detectors)
                writer.WriteStringValue(d);
            writer.WriteEndArray();
            writer.WriteString("text", text);
            writer.WriteEndObject();
        }
        return ms.ToArray();
    }

    private static MultipartFormDataContent BuildBinaryMultipart(
        string requestId,
        string policyRevision,
        string modality,
        IReadOnlyList<string> detectors,
        ReadOnlyMemory<byte> bytes,
        string contentType,
        string sha256Digest)
    {
        var form = new MultipartFormDataContent();
        using var metaStream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(metaStream))
        {
            writer.WriteStartObject();
            writer.WriteString("schemaVersion", SecurityContractVersions.InspectionV1);
            writer.WriteString("requestId", requestId);
            writer.WriteString("policyRevision", policyRevision);
            writer.WriteString("modality", modality);
            writer.WritePropertyName("detectors");
            writer.WriteStartArray();
            foreach (var d in detectors)
                writer.WriteStringValue(d);
            writer.WriteEndArray();
            writer.WriteString("contentSha256", sha256Digest);
            writer.WriteString("contentType", contentType);
            writer.WriteEndObject();
        }
        var metaContent = new ByteArrayContent(metaStream.ToArray());
        metaContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        form.Add(metaContent, "metadata");

        var fileContent = new ByteArrayContent(bytes.ToArray());
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        form.Add(fileContent, "file", "payload.bin");
        return form;
    }

    private static void ValidateOptions(SecurityWorkerClientOptions options)
    {
        if (options.Endpoint is null || !options.Endpoint.IsAbsoluteUri)
            throw new ArgumentException("A valid absolute Endpoint URI is required.", nameof(options));
        if (!string.IsNullOrEmpty(options.Endpoint.UserInfo))
            throw new ArgumentException("Worker Endpoint must not contain userinfo credentials.", nameof(options));
        if (string.Equals(options.Endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            if (!options.AllowInsecureLoopbackHttpInDevelopment || !options.Endpoint.IsLoopback)
                throw new ArgumentException("Plaintext HTTP worker endpoint is only permitted on loopback when explicitly enabled for development.", nameof(options));
        }
        else if (!string.Equals(options.Endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Worker Endpoint must use HTTPS (or explicitly approved development loopback HTTP).", nameof(options));
        }
        if (options.RequireAuthenticatedTransport && string.IsNullOrWhiteSpace(options.BearerToken))
            throw new ArgumentException("BearerToken is required when RequireAuthenticatedTransport is true.", nameof(options));
        if (options.MaxTextBytes <= 0 || options.MaxImageBytes <= 0 || options.MaxFileBytes <= 0 ||
            options.MaxResponseBytes <= 0 || options.MaxConcurrentRequests < 1 || options.RequestTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Worker client limits and timeouts must be positive.");
    }

    private static bool ValidateCommonRequest(string requestId, string policyRevision, IReadOnlyList<string> detectors, out string errorReason)
    {
        if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 128)
        { errorReason = "invalid_request_id"; return false; }
        if (string.IsNullOrWhiteSpace(policyRevision) || policyRevision.Length > 64)
        { errorReason = "invalid_policy_revision"; return false; }
        if (detectors is null || detectors.Count == 0 || detectors.Count > 16)
        { errorReason = "invalid_detectors"; return false; }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in detectors)
        {
            if (string.IsNullOrWhiteSpace(d) || !KnownDetectors.Contains(d) || !seen.Add(d))
            { errorReason = "invalid_detectors"; return false; }
        }
        errorReason = string.Empty;
        return true;
    }

    private static SecurityInspectionResponse FailClosed(string requestId, SecurityInspectionStatus status, string reasonCode) =>
        new(
            SecurityContractVersions.InspectionV1,
            requestId ?? string.Empty,
            status,
            Array.Empty<SecurityDetectorStatus>(),
            Array.Empty<SecurityFinding>(),
            ReasonCode: reasonCode);

    private static bool TryGetString(JsonElement obj, string name, int maxLen, out string value)
    {
        value = string.Empty;
        if (!obj.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String)
            return false;
        var s = prop.GetString();
        if (string.IsNullOrWhiteSpace(s) || s.Length > maxLen)
            return false;
        value = s;
        return true;
    }

    private static bool TryGetUnitDouble(JsonElement obj, string name, out double value)
    {
        value = 0;
        if (!obj.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.Number)
            return false;
        if (!prop.TryGetDouble(out var d) || !double.IsFinite(d) || d is < 0.0 or > 1.0)
            return false;
        value = d;
        return true;
    }

    private static SecurityCapabilityState? ParseCapabilityState(string s) => s switch
    {
        "disabled" => SecurityCapabilityState.Disabled,
        "configured" => SecurityCapabilityState.Configured,
        "provisioning" => SecurityCapabilityState.Provisioning,
        "ready" => SecurityCapabilityState.Ready,
        "degraded" => SecurityCapabilityState.Degraded,
        "unavailable" => SecurityCapabilityState.Unavailable,
        _ => null
    };

    private static bool TryParseInspectionStatus(string s, out SecurityInspectionStatus status)
    {
        status = s switch
        {
            "completed" => SecurityInspectionStatus.Completed,
            "partial" => SecurityInspectionStatus.Partial,
            "degraded" => SecurityInspectionStatus.Degraded,
            "unavailable" => SecurityInspectionStatus.Unavailable,
            "failed" => SecurityInspectionStatus.Failed,
            "limit_exceeded" => SecurityInspectionStatus.LimitExceeded,
            "unsupported" => SecurityInspectionStatus.Unsupported,
            "not_run" => SecurityInspectionStatus.NotRun,
            _ => (SecurityInspectionStatus)(-1)
        };
        return Enum.IsDefined(status);
    }

    private static bool TryParseFindingKind(string s, out SecurityFindingKind kind)
    {
        kind = s switch
        {
            "text_span" => SecurityFindingKind.TextSpan,
            "image_region" => SecurityFindingKind.ImageRegion,
            "malware" => SecurityFindingKind.Malware,
            "injection" => SecurityFindingKind.Injection,
            _ => (SecurityFindingKind)(-1)
        };
        return Enum.IsDefined(kind);
    }

    private static bool TryParseMalwareStatus(string s, out SecurityMalwareStatus status)
    {
        status = s switch
        {
            "clean" => SecurityMalwareStatus.Clean,
            "detected" => SecurityMalwareStatus.Detected,
            "unavailable" => SecurityMalwareStatus.Unavailable,
            "unsupported" => SecurityMalwareStatus.Unsupported,
            "limit_exceeded" => SecurityMalwareStatus.LimitExceeded,
            _ => (SecurityMalwareStatus)(-1)
        };
        return Enum.IsDefined(status);
    }

    private static bool TryParsePolicyAction(string s, out SecurityPolicyAction action)
    {
        action = s switch
        {
            "allow" => SecurityPolicyAction.Allow,
            "mask" => SecurityPolicyAction.Mask,
            "redact" => SecurityPolicyAction.Redact,
            "block" => SecurityPolicyAction.Block,
            "review" => SecurityPolicyAction.Review,
            _ => (SecurityPolicyAction)(-1)
        };
        return Enum.IsDefined(action);
    }

    private static string ComputeSha256Hex(ReadOnlySpan<byte> data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool IsValidSha256Hex(string? hex)
    {
        if (string.IsNullOrEmpty(hex) || hex.Length != 64)
            return false;
        foreach (var ch in hex)
        {
            if (ch is not ((>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F')))
                return false;
        }
        return true;
    }
}
