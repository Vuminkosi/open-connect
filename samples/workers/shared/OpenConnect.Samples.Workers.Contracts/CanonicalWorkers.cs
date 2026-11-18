namespace OpenConnect.Samples.Workers.Contracts;

public sealed record WorkerSampleSpec(
    string ShortCode,
    string WorkerId,
    string SampleRelativePath,
    string ProductionDockerfilePath,
    string ProductionComposePath,
    string DockerHubImage,
    string? ModelCacheVolume,
    int DefaultHostPort,
    bool IsModelBacked,
    IReadOnlyList<string> AcceptanceIds);

public static class CanonicalWorkers
{
    public static readonly IReadOnlyList<WorkerSampleSpec> All =
    [
        new("SEC", "security", "samples/workers/security", "workers/security/Dockerfile", "workers/security/compose.yaml",
            "vuminkosimatibe/openconnect-worker-security", "oc_security_models", 8080, true,
            ["WSE2E-AT-001", "WSE2E-AT-002", "WSE2E-AT-003", "WSE2E-AT-004", "WSE2E-AT-005", "WSE2E-AT-006", "WSE2E-AT-007", "WSE2E-AT-008"]),
        new("CLS", "classification", "samples/workers/classification", "workers/classification/Dockerfile", "workers/classification/compose.yaml",
            "vuminkosimatibe/openconnect-worker-classification", "oc_classification_models", 8081, true,
            ["WSE2E-AT-009", "WSE2E-AT-010", "WSE2E-AT-011", "WSE2E-AT-012", "WSE2E-AT-013", "WSE2E-AT-014"]),
        new("NLLB", "nllb", "samples/workers/nllb", "workers/nllb/Dockerfile", "workers/nllb/compose.yaml",
            "vuminkosimatibe/openconnect-worker-nllb", "oc_nllb_models", 8082, true,
            ["WSE2E-AT-015", "WSE2E-AT-016", "WSE2E-AT-017", "WSE2E-AT-018", "WSE2E-AT-019", "WSE2E-AT-020"]),
        new("INF", "inference", "samples/workers/inference", "workers/inference/Dockerfile", "workers/inference/compose.yaml",
            "vuminkosimatibe/openconnect-worker-inference", "oc_inference_models", 8083, true,
            ["WSE2E-AT-021", "WSE2E-AT-022", "WSE2E-AT-023", "WSE2E-AT-024", "WSE2E-AT-025", "WSE2E-AT-026", "WSE2E-AT-027", "WSE2E-AT-028"]),
        new("STT", "transcription", "samples/workers/transcription", "workers/transcription/Dockerfile", "workers/transcription/compose.yaml",
            "vuminkosimatibe/openconnect-worker-transcription", "oc_transcription_models", 8084, true,
            ["WSE2E-AT-029", "WSE2E-AT-030", "WSE2E-AT-031", "WSE2E-AT-032", "WSE2E-AT-033", "WSE2E-AT-034", "WSE2E-AT-035"]),
        new("RTV", "realtime-voice", "samples/workers/realtime-voice", "workers/realtime-voice/Dockerfile", "workers/realtime-voice/compose.yaml",
            "vuminkosimatibe/openconnect-worker-realtime-voice", "oc_realtime_voice_models", 8085, true,
            ["WSE2E-AT-036", "WSE2E-AT-037", "WSE2E-AT-038", "WSE2E-AT-039", "WSE2E-AT-040", "WSE2E-AT-041", "WSE2E-AT-042", "WSE2E-AT-043", "WSE2E-AT-044"]),
        new("KOK", "kokoro", "samples/workers/kokoro", "workers/kokoro/Dockerfile", "workers/kokoro/compose.yaml",
            "vuminkosimatibe/openconnect-worker-kokoro", "oc_kokoro_models", 8086, true,
            ["WSE2E-AT-045", "WSE2E-AT-046", "WSE2E-AT-047", "WSE2E-AT-048", "WSE2E-AT-049", "WSE2E-AT-050", "WSE2E-AT-051"]),
        new("MMS", "mms-tts", "samples/workers/mms-tts", "workers/mms-tts/Dockerfile", "workers/mms-tts/compose.yaml",
            "vuminkosimatibe/openconnect-worker-mms-tts", "oc_mms_models", 8087, true,
            ["WSE2E-AT-052", "WSE2E-AT-053", "WSE2E-AT-054", "WSE2E-AT-055", "WSE2E-AT-056", "WSE2E-AT-057", "WSE2E-AT-058"]),
        new("NAV", "navigator-playwright", "samples/workers/navigator-playwright", "workers/navigator-playwright/Dockerfile", "workers/navigator-playwright/compose.yaml",
            "vuminkosimatibe/openconnect-worker-navigator-playwright", null, 8088, false,
            ["WSE2E-AT-059", "WSE2E-AT-060", "WSE2E-AT-061", "WSE2E-AT-062", "WSE2E-AT-063", "WSE2E-AT-064", "WSE2E-AT-065"]),
        new("OPS", "operational-agents", "samples/workers/operational-agents", "src/OpenConnect.WorkerHosts.Operational/Dockerfile", "src/OpenConnect.WorkerHosts.Operational/compose.yaml",
            "vuminkosimatibe/openconnect-worker-operational-agents", null, 8089, false,
            ["WSE2E-AT-066", "WSE2E-AT-067", "WSE2E-AT-068", "WSE2E-AT-069", "WSE2E-AT-070", "WSE2E-AT-071", "WSE2E-AT-072"]),
        new("OUT", "outbox", "samples/workers/outbox", "src/OpenConnect.WorkerHosts.Outbox/Dockerfile", "src/OpenConnect.WorkerHosts.Outbox/compose.yaml",
            "vuminkosimatibe/openconnect-worker-outbox", null, 8090, false,
            ["WSE2E-AT-073", "WSE2E-AT-074", "WSE2E-AT-075", "WSE2E-AT-076", "WSE2E-AT-077", "WSE2E-AT-078", "WSE2E-AT-079", "WSE2E-AT-080"])
    ];

    public static readonly IReadOnlySet<string> ExtraSampleNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "voice-vad-client-server",
        "all-workers"
    };

    public static WorkerSampleSpec? FindByWorkerId(string workerId) =>
        All.FirstOrDefault(w => string.Equals(w.WorkerId, workerId, StringComparison.Ordinal));

    public static bool IsValidWorkerOrSample(string name) =>
        FindByWorkerId(name) is not null || ExtraSampleNames.Contains(name);
}
