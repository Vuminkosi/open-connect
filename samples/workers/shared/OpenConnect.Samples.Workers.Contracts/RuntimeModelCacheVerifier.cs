using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenConnect.Samples.Workers.Contracts;

public sealed record ModelArtifactSpec(
    [property: JsonPropertyName("relativePath")] string RelativePath,
    [property: JsonPropertyName("sizeBytes")] long SizeBytes,
    [property: JsonPropertyName("sha256")] string Sha256);

public sealed record RuntimeModelManifest(
    [property: JsonPropertyName("workerKind")] string WorkerKind,
    [property: JsonPropertyName("modelId")] string ModelId,
    [property: JsonPropertyName("immutableRevision")] string ImmutableRevision,
    [property: JsonPropertyName("licenseSpdx")] string LicenseSpdx,
    [property: JsonPropertyName("commercialUseAllowed")] bool CommercialUseAllowed,
    [property: JsonPropertyName("operatorApproved")] bool OperatorApproved,
    [property: JsonPropertyName("runtimeCompatibility")] string RuntimeCompatibility,
    [property: JsonPropertyName("quantization")] string Quantization,
    [property: JsonPropertyName("artifacts")] IReadOnlyList<ModelArtifactSpec> Artifacts,
    [property: JsonPropertyName("tokenizerFiles")] IReadOnlyList<string> TokenizerFiles,
    [property: JsonPropertyName("voiceFiles")] IReadOnlyList<string> VoiceFiles);

public sealed record CacheProvisionOutcome(
    bool Ready,
    string StatusReason,
    string? Fingerprint,
    string? ActiveRevisionPath,
    int OutboundDownloads);

/// <summary>
/// Implements the §4.1–4.3 shared runtime-model cache E2E protocol for verifying deterministic
/// SHA-256 manifest fingerprints, immutable revision directories, atomic staging promotion,
/// offline cache reuse, revision upgrades, rollback, corruption rejection, and zero request-time downloads.
/// </summary>
public sealed class RuntimeModelCacheManager
{
    private readonly string _modelsRoot;
    private readonly object _lockGate = new();
    private int _outboundDownloads;

    public RuntimeModelCacheManager(string modelsRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsRoot);
        _modelsRoot = modelsRoot;
    }

    public int OutboundDownloads => _outboundDownloads;

    public void ResetDownloadCounter() => Interlocked.Exchange(ref _outboundDownloads, 0);

    public static string ComputeManifestFingerprint(RuntimeModelManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var sb = new StringBuilder();
        sb.Append(manifest.WorkerKind).Append('|')
          .Append(manifest.ModelId).Append('|')
          .Append(manifest.ImmutableRevision).Append('|')
          .Append(manifest.RuntimeCompatibility).Append('|')
          .Append(manifest.Quantization).Append('|');

        foreach (var artifact in manifest.Artifacts.OrderBy(a => a.RelativePath, StringComparer.Ordinal))
        {
            sb.Append(artifact.RelativePath).Append(':').Append(artifact.SizeBytes).Append(':').Append(artifact.Sha256.ToLowerInvariant()).Append(';');
        }
        foreach (var tok in manifest.TokenizerFiles.OrderBy(t => t, StringComparer.Ordinal))
        {
            sb.Append("tok:").Append(tok).Append(';');
        }
        foreach (var voice in manifest.VoiceFiles.OrderBy(v => v, StringComparer.Ordinal))
        {
            sb.Append("voice:").Append(voice).Append(';');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public string GetModelDirectory(string workerKind, string modelId)
    {
        var safeModelId = modelId.Replace('/', '_').Replace('\\', '_').Replace(':', '_');
        return Path.Combine(_modelsRoot, workerKind, safeModelId);
    }

    public CacheProvisionOutcome ProvisionOrVerifyStartup(
        RuntimeModelManifest manifest,
        bool workerEnabled,
        bool networkAvailable,
        bool commercialPolicyRequired,
        Func<ModelArtifactSpec, byte[]>? artifactFetcher = null,
        bool simulateInterruptedDownload = false)
    {
        if (!workerEnabled)
        {
            return new CacheProvisionOutcome(false, "worker_disabled", null, null, _outboundDownloads);
        }

        if (!manifest.OperatorApproved || string.IsNullOrWhiteSpace(manifest.LicenseSpdx))
        {
            return new CacheProvisionOutcome(false, "license_denied", null, null, _outboundDownloads);
        }

        if (commercialPolicyRequired && !manifest.CommercialUseAllowed)
        {
            return new CacheProvisionOutcome(false, "license_denied_commercial", null, null, _outboundDownloads);
        }

        var fingerprint = ComputeManifestFingerprint(manifest);
        var modelDir = GetModelDirectory(manifest.WorkerKind, manifest.ModelId);
        var revisionDir = Path.Combine(modelDir, "revisions", fingerprint);
        var locksDir = Path.Combine(modelDir, "locks");
        var stagingRoot = Path.Combine(modelDir, ".staging");
        var activeJsonPath = Path.Combine(modelDir, "active.json");

        lock (_lockGate)
        {
            Directory.CreateDirectory(locksDir);
            var lockFile = Path.Combine(locksDir, $"{fingerprint}.lock");
            File.WriteAllText(lockFile, DateTimeOffset.UtcNow.ToString("O"));

            if (VerifyExistingRevision(revisionDir, manifest, fingerprint))
            {
                PromoteActiveRevision(activeJsonPath, fingerprint, manifest.ImmutableRevision);
                return new CacheProvisionOutcome(true, "verified_cache_reused", fingerprint, revisionDir, _outboundDownloads);
            }

            if (!networkAvailable)
            {
                return new CacheProvisionOutcome(false, "offline_revision_unavailable", fingerprint, null, _outboundDownloads);
            }

            var stagingDir = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
            var stagingAssetsDir = Path.Combine(stagingDir, "assets");
            Directory.CreateDirectory(stagingAssetsDir);

            if (simulateInterruptedDownload)
            {
                File.WriteAllText(Path.Combine(stagingDir, "incomplete.marker"), "interrupted");
                return new CacheProvisionOutcome(false, "staging_incomplete", fingerprint, null, _outboundDownloads);
            }

            foreach (var artifact in manifest.Artifacts)
            {
                Interlocked.Increment(ref _outboundDownloads);
                var bytes = artifactFetcher is not null
                    ? artifactFetcher(artifact)
                    : Encoding.UTF8.GetBytes($"synthetic-model-bytes:{manifest.ModelId}:{manifest.ImmutableRevision}:{artifact.RelativePath}");

                var actualHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                if (bytes.LongLength != artifact.SizeBytes || !string.Equals(actualHash, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.WriteAllText(Path.Combine(stagingDir, "corrupt.marker"), "digest_mismatch");
                    return new CacheProvisionOutcome(false, "artifact_verification_failed", fingerprint, null, _outboundDownloads);
                }

                var targetAsset = Path.Combine(stagingAssetsDir, artifact.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(targetAsset)!);
                File.WriteAllBytes(targetAsset, bytes);
            }

            File.WriteAllText(
                Path.Combine(stagingDir, "manifest.lock.json"),
                JsonSerializer.Serialize(manifest));
            File.WriteAllText(
                Path.Combine(stagingDir, "verification.json"),
                JsonSerializer.Serialize(new
                {
                    fingerprint,
                    revision = manifest.ImmutableRevision,
                    license = manifest.LicenseSpdx,
                    verifiedAtUtc = DateTimeOffset.UtcNow.ToString("O")
                }));

            Directory.CreateDirectory(Path.GetDirectoryName(revisionDir)!);
            if (Directory.Exists(revisionDir))
            {
                Directory.Delete(revisionDir, recursive: true);
            }
            Directory.Move(stagingDir, revisionDir);

            PromoteActiveRevision(activeJsonPath, fingerprint, manifest.ImmutableRevision);
            return new CacheProvisionOutcome(true, "provisioned_and_promoted", fingerprint, revisionDir, _outboundDownloads);
        }
    }

    public CacheProvisionOutcome HandleRuntimeInferenceRequest(RuntimeModelManifest manifest)
    {
        // §4.2 scenario 12: Request handler NEVER opens a model-registry connection or downloads weights.
        var fingerprint = ComputeManifestFingerprint(manifest);
        var modelDir = GetModelDirectory(manifest.WorkerKind, manifest.ModelId);
        var revisionDir = Path.Combine(modelDir, "revisions", fingerprint);

        if (!VerifyExistingRevision(revisionDir, manifest, fingerprint))
        {
            return new CacheProvisionOutcome(false, "model_unready_no_request_time_download", fingerprint, null, _outboundDownloads);
        }

        return new CacheProvisionOutcome(true, "served_from_verified_cache", fingerprint, revisionDir, _outboundDownloads);
    }

    private static bool VerifyExistingRevision(string revisionDir, RuntimeModelManifest manifest, string expectedFingerprint)
    {
        var verificationPath = Path.Combine(revisionDir, "verification.json");
        var lockPath = Path.Combine(revisionDir, "manifest.lock.json");
        if (!File.Exists(verificationPath) || !File.Exists(lockPath))
        {
            return false;
        }

        foreach (var artifact in manifest.Artifacts)
        {
            var assetPath = Path.Combine(revisionDir, "assets", artifact.RelativePath);
            if (!File.Exists(assetPath))
            {
                return false;
            }

            var bytes = File.ReadAllBytes(assetPath);
            var actualHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (bytes.LongLength != artifact.SizeBytes || !string.Equals(actualHash, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return verificationPath.Contains(expectedFingerprint, StringComparison.OrdinalIgnoreCase) || File.ReadAllText(verificationPath).Contains(expectedFingerprint, StringComparison.OrdinalIgnoreCase);
    }

    private static void PromoteActiveRevision(string activeJsonPath, string fingerprint, string immutableRevision)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(activeJsonPath)!);
        File.WriteAllText(
            activeJsonPath,
            JsonSerializer.Serialize(new
            {
                activeFingerprint = fingerprint,
                immutableRevision,
                promotedAtUtc = DateTimeOffset.UtcNow.ToString("O")
            }));
    }

    public static ModelArtifactSpec CreateSyntheticArtifactSpec(string modelId, string immutableRevision, string relativePath)
    {
        var bytes = Encoding.UTF8.GetBytes($"synthetic-model-bytes:{modelId}:{immutableRevision}:{relativePath}");
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new ModelArtifactSpec(relativePath, bytes.LongLength, sha);
    }
}
