using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using OpenConnect.Persistence.EFCore.Configuration;
using OpenConnect.Persistence.EFCore.DependencyInjection;
using OpenConnect.Privacy;
using OpenConnect.Samples.Workers.Contracts;
using OpenConnect.Samples.Workers.Host;
using OpenConnect.Transport.Nats;
using Xunit;

namespace OpenConnect.Samples.Workers.TestKit;

public sealed class SharedWorkerHarnessTests
{
    [Fact]
    public void CanonicalWorkers_ShouldDefineExactlyElevenWorkersAndEightyAcceptanceIds()
    {
        CanonicalWorkers.All.Should().HaveCount(11);
        CanonicalWorkers.All.Select(w => w.DockerHubImage)
            .Should().OnlyContain(img => img.StartsWith("vuminkosimatibe/openconnect-worker-", StringComparison.Ordinal));

        var allAcceptanceIds = CanonicalWorkers.All.SelectMany(w => w.AcceptanceIds).ToList();
        allAcceptanceIds.Should().HaveCount(80);
        allAcceptanceIds.Should().OnlyHaveUniqueItems();
        allAcceptanceIds.First().Should().Be("WSE2E-AT-001");
        allAcceptanceIds.Last().Should().Be("WSE2E-AT-080");

        // Navigator, OperationalAgents, and Outbox must not require model cache volumes (§8 WSE2E-REQ-011)
        CanonicalWorkers.FindByWorkerId("navigator-playwright")!.ModelCacheVolume.Should().BeNull();
        CanonicalWorkers.FindByWorkerId("operational-agents")!.ModelCacheVolume.Should().BeNull();
        CanonicalWorkers.FindByWorkerId("outbox")!.ModelCacheVolume.Should().BeNull();

        // All 8 Python model-backed workers have distinct named model volumes
        var modelVolumes = CanonicalWorkers.All.Where(w => w.IsModelBacked).Select(w => w.ModelCacheVolume).ToList();
        modelVolumes.Should().HaveCount(8);
        modelVolumes.Should().NotContainNulls();
        modelVolumes.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void SampleTiersAndProviders_ShouldNormalizeValidInputsAndRejectUnknownOrDestructiveFlags()
    {
        SampleTiersAndProviders.TryNormalizeTier("static", out var t0).Should().BeTrue();
        t0.Should().Be("static");
        SampleTiersAndProviders.TryNormalizeTier("t1", out var t1).Should().BeTrue();
        t1.Should().Be("contract");
        SampleTiersAndProviders.TryNormalizeTier("integration", out var t3).Should().BeTrue();
        t3.Should().Be("integration");
        SampleTiersAndProviders.TryNormalizeTier("model-live", out var t4).Should().BeTrue();
        t4.Should().Be("model-live");
        SampleTiersAndProviders.TryNormalizeTier("offline-revision", out var t5).Should().BeTrue();
        t5.Should().Be("offline-revision");
        SampleTiersAndProviders.TryNormalizeTier("release", out var t6).Should().BeTrue();
        t6.Should().Be("release");
        SampleTiersAndProviders.TryNormalizeTier("unknown-tier", out _).Should().BeFalse();

        SampleTiersAndProviders.IsValidProvider("sqlite").Should().BeTrue();
        SampleTiersAndProviders.IsValidProvider("postgres").Should().BeTrue();
        SampleTiersAndProviders.IsValidProvider("sqlserver").Should().BeTrue();
        SampleTiersAndProviders.IsValidProvider("oracle").Should().BeFalse();

        SampleTiersAndProviders.IsDestructiveFlag("-v").Should().BeTrue();
        SampleTiersAndProviders.IsDestructiveFlag("--volumes").Should().BeTrue();
        SampleTiersAndProviders.IsDestructiveFlag("--remove-orphans").Should().BeFalse();

        SampleTiersAndProviders.ComposeProjectNameFor("security").Should().Be("ocw-e2e-security");
        Action unknownWorker = () => SampleTiersAndProviders.ComposeProjectNameFor("not-a-worker");
        unknownWorker.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task SecurityFirewall_RulesOnly_ShouldMaskStructuredPii_StoreInVault_AndBlockCrossTenantOrWorkloadRestore()
    {
        var coordinator = new SampleGatewayCoordinator(
            enabledDetectors: [SecurityDetectorKind.Rules],
            requiredCoverages: [SecurityCoverage.StructuredPii]);

        var tenantA = new TenantId(Ulid.NewUlid());
        var tenantB = new TenantId(Ulid.NewUlid());
        var userA = new PrincipalId(Ulid.NewUlid());
        var sessionA = new SessionId(Ulid.NewUlid());

        var prompt = "Contact alice.synthetic@example.test or +1-555-234-5678 with key sk-SyntheticTestKey123456.";
        var decision = await coordinator.EvaluateAndSendToExternalSinkAsync(prompt, tenantA, userA, sessionA);

        decision.Allowed.Should().BeTrue();
        decision.Action.Should().Be(SecurityPolicyAction.Mask);
        decision.DetectedEntities.Should().HaveCount(3);
        decision.SanitizedContent.Should().NotContain("alice.synthetic@example.test");
        decision.SanitizedContent.Should().NotContain("555-234-5678");
        decision.SanitizedContent.Should().NotContain("sk-SyntheticTestKey123456");
        coordinator.ExternalSink.ReceivedBytes.Should().BeGreaterThan(0);

        // Extract first token and verify authorized restoration succeeds for Tenant A interactive user
        var tokenStart = decision.SanitizedContent!.IndexOf("[OC_", StringComparison.Ordinal);
        var tokenEnd = decision.SanitizedContent.IndexOf(']', tokenStart);
        var token = decision.SanitizedContent.Substring(tokenStart, tokenEnd - tokenStart + 1);

        var restoredByOwner = await coordinator.RestoreTokenAsync(token, tenantA, userA, sessionA, AgentIdentityMode.ActiveUser);
        restoredByOwner.Should().Be("alice.synthetic@example.test");

        // Cross-tenant restoration MUST be denied
        var restoredByOtherTenant = await coordinator.RestoreTokenAsync(token, tenantB, userA, sessionA, AgentIdentityMode.ActiveUser);
        restoredByOtherTenant.Should().BeNull();

        // Operational workload identity restoration MUST be denied
        var restoredByWorkload = await coordinator.RestoreTokenAsync(token, tenantA, userA, sessionA, AgentIdentityMode.WorkloadIdentity);
        restoredByWorkload.Should().BeNull();
    }

    [Fact]
    public async Task SecurityFirewall_MissingRequiredSemanticOrImageDetector_ShouldFailClosedWithZeroOutboundBytes()
    {
        var coordinator = new SampleGatewayCoordinator(
            enabledDetectors: [SecurityDetectorKind.Rules], // GLiNER disabled
            requiredCoverages: [SecurityCoverage.StructuredPii, SecurityCoverage.SemanticPii]);

        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        var session = new SessionId(Ulid.NewUlid());

        var decision = await coordinator.EvaluateAndSendToExternalSinkAsync(
            "Patient John Doe visited clinic.", tenant, principal, session);

        decision.Allowed.Should().BeFalse();
        decision.Action.Should().Be(SecurityPolicyAction.Block);
        decision.SanitizedContent.Should().BeNull();
        coordinator.ExternalSink.ReceivedBytes.Should().Be(0);
    }

    [Fact]
    public void RuntimeModelCacheProtocol_AllTwelveScenarios_ShouldHold()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "ocw-cache-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var manager = new RuntimeModelCacheManager(tempRoot);
            var artifactV1 = RuntimeModelCacheManager.CreateSyntheticArtifactSpec("kokoro-82m", "rev-1", "weights/model.onnx");
            var manifestV1 = new RuntimeModelManifest(
                WorkerKind: "kokoro",
                ModelId: "kokoro-82m",
                ImmutableRevision: "rev-1",
                LicenseSpdx: "Apache-2.0",
                CommercialUseAllowed: true,
                OperatorApproved: true,
                RuntimeCompatibility: "onnxruntime-1.19",
                Quantization: "fp16",
                Artifacts: [artifactV1],
                TokenizerFiles: ["tokenizer.json"],
                VoiceFiles: ["voices/af_heart.bin"]);

            // Scenario 2: Worker disabled -> zero downloads, no directory
            var disabledOutcome = manager.ProvisionOrVerifyStartup(manifestV1, workerEnabled: false, networkAvailable: true, commercialPolicyRequired: false);
            disabledOutcome.Ready.Should().BeFalse();
            disabledOutcome.StatusReason.Should().Be("worker_disabled");
            manager.OutboundDownloads.Should().Be(0);

            // Scenario 12: Live inference request before provisioning -> typed unready, zero downloads
            var unreadyInference = manager.HandleRuntimeInferenceRequest(manifestV1);
            unreadyInference.Ready.Should().BeFalse();
            unreadyInference.StatusReason.Should().Be("model_unready_no_request_time_download");
            manager.OutboundDownloads.Should().Be(0);

            // Scenario 11: Offline with no matching revision -> unready
            var offlineMiss = manager.ProvisionOrVerifyStartup(manifestV1, workerEnabled: true, networkAvailable: false, commercialPolicyRequired: false);
            offlineMiss.Ready.Should().BeFalse();
            offlineMiss.StatusReason.Should().Be("offline_revision_unavailable");
            manager.OutboundDownloads.Should().Be(0);

            // Scenario 3: First enabled startup provisions, verifies, and promotes atomically
            var firstStart = manager.ProvisionOrVerifyStartup(manifestV1, workerEnabled: true, networkAvailable: true, commercialPolicyRequired: true);
            firstStart.Ready.Should().BeTrue();
            firstStart.StatusReason.Should().Be("provisioned_and_promoted");
            manager.OutboundDownloads.Should().Be(1);

            // Scenario 4 & 5: Offline restart and image upgrade with same manifest -> zero outbound downloads
            manager.ResetDownloadCounter();
            var offlineRestart = manager.ProvisionOrVerifyStartup(manifestV1, workerEnabled: true, networkAvailable: false, commercialPolicyRequired: true);
            offlineRestart.Ready.Should().BeTrue();
            offlineRestart.StatusReason.Should().Be("verified_cache_reused");
            manager.OutboundDownloads.Should().Be(0);

            // Scenario 6: Model revision change creates new immutable directory without overwriting rev-1
            var artifactV2 = RuntimeModelCacheManager.CreateSyntheticArtifactSpec("kokoro-82m", "rev-2", "weights/model.onnx");
            var manifestV2 = manifestV1 with { ImmutableRevision = "rev-2", Artifacts = [artifactV2] };

            // Scenario 9: Interrupted download preserves prior revision and marks staging incomplete
            var interrupted = manager.ProvisionOrVerifyStartup(
                manifestV2, workerEnabled: true, networkAvailable: true, commercialPolicyRequired: true, simulateInterruptedDownload: true);
            interrupted.Ready.Should().BeFalse();
            interrupted.StatusReason.Should().Be("staging_incomplete");

            var rev2Start = manager.ProvisionOrVerifyStartup(manifestV2, workerEnabled: true, networkAvailable: true, commercialPolicyRequired: true);
            rev2Start.Ready.Should().BeTrue();
            rev2Start.Fingerprint.Should().NotBe(firstStart.Fingerprint);
            Directory.Exists(firstStart.ActiveRevisionPath!).Should().BeTrue("prior revision directory must remain intact for rollback");

            // Scenario 7: Revert revision offline -> reuses rev-1 with zero downloads
            manager.ResetDownloadCounter();
            var rollbackOffline = manager.ProvisionOrVerifyStartup(manifestV1, workerEnabled: true, networkAvailable: false, commercialPolicyRequired: true);
            rollbackOffline.Ready.Should().BeTrue();
            rollbackOffline.StatusReason.Should().Be("verified_cache_reused");
            manager.OutboundDownloads.Should().Be(0);

            // Scenario 8: Corrupt file in rev-2 -> rejected on offline startup
            var rev2AssetPath = Path.Combine(rev2Start.ActiveRevisionPath!, "assets", "weights", "model.onnx");
            File.WriteAllText(rev2AssetPath, "corrupted-bytes");
            var corruptCheck = manager.ProvisionOrVerifyStartup(manifestV2, workerEnabled: true, networkAvailable: false, commercialPolicyRequired: true);
            corruptCheck.Ready.Should().BeFalse();
            corruptCheck.StatusReason.Should().Be("offline_revision_unavailable");
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void WorkerContracts_ClassificationAndTranslationAndEnvelope_ShouldEnforceSecurityInvariants()
    {
        var allowedLabels = new HashSet<string>(StringComparer.Ordinal)
        {
            "billing-question", "technical-support", "general-question", "unknown"
        };
        var locales = new HashSet<string>(StringComparer.Ordinal) { "en", "es", "fr" };

        // Malicious classifier response attempting to grant tools is flagged and cannot elevate privileges
        var maliciousJson = """{"label":"billing-question","score":0.92,"locale":"en","toolGrant":"admin.delete_all","requiredScopes":["root"]}""";
        var outcome = WorkerContractAssertions.EvaluateAdvisoryClassificationResponse(maliciousJson, allowedLabels, locales);
        outcome.Valid.Should().BeTrue();
        outcome.AttemptedPrivilegeEscalationBlocked.Should().BeTrue();

        // Unsupported locale abstains/fails
        var unsupportedLocaleJson = """{"label":"billing-question","score":0.85,"locale":"zz-unknown"}""";
        var localeOutcome = WorkerContractAssertions.EvaluateAdvisoryClassificationResponse(unsupportedLocaleJson, allowedLabels, locales);
        localeOutcome.Valid.Should().BeFalse();
        localeOutcome.ErrorCode.Should().Be("unsupported_locale");

        // NLLB non-commercial checkpoint blocked under commercial policy
        var allowedPairs = new HashSet<string>(StringComparer.Ordinal) { "eng_Latn->fra_Latn" };
        var nllbBlocked = WorkerContractAssertions.ValidateTranslationRequest(
            "Hello world", "eng_Latn", "fra_Latn", allowedPairs, 512, commercialDeployment: true, checkpointCommercialAllowed: false);
        nllbBlocked.Allowed.Should().BeFalse();
        nllbBlocked.ErrorCode.Should().Be("license_denied");

        // WorkerEnvelope metadata-only validation
        var now = DateTimeOffset.UtcNow;
        var validEnvelopeJson = $$"""
        {
          "schemaVersion": "openconnect.v1",
          "messageId": "{{Guid.NewGuid()}}",
          "taskId": "01JCF3G72M9W4P7RVAXB3C9D1E",
          "kind": "security.scan",
          "issuedAt": "{{now:O}}",
          "expiresAt": "{{now.AddMinutes(5):O}}",
          "lease": { "leaseId": "{{Guid.NewGuid()}}", "fencingToken": 1 },
          "payloadRef": "vault://tasks/01JCF3G72M9W4P7RVAXB3C9D1E"
        }
        """;
        var envelope = WorkerEnvelope.Parse(System.Text.Encoding.UTF8.GetBytes(validEnvelopeJson));
        envelope.Kind.Should().Be("security.scan");
    }

    [Fact]
    public async Task PersistenceAndHostingExclusivity_ShouldVerifyModelAndRejectConflictingModes()
    {
        // Embedded vs Standalone exclusivity guard
        Action conflictOps = () => SampleGatewayCoordinator.ValidateHostingExclusivity(
            embeddedOperationalEnabled: true, standaloneOperationalEnabled: true,
            embeddedOutboxEnabled: false, standaloneOutboxEnabled: false);
        conflictOps.Should().Throw<InvalidOperationException>().WithMessage("*worker_hosting_mode_conflict*");

        Action conflictOutbox = () => SampleGatewayCoordinator.ValidateHostingExclusivity(
            embeddedOperationalEnabled: false, standaloneOperationalEnabled: false,
            embeddedOutboxEnabled: true, standaloneOutboxEnabled: true);
        conflictOutbox.Should().Throw<InvalidOperationException>().WithMessage("*worker_hosting_mode_conflict*");

        // Verify all 3 sample persistence provider ApplicationDbContext models map all 12 OpenConnect entities
        using var pgContext = new OpenConnect.Samples.Persistence.Postgres.ApplicationDbContextFactory().CreateDbContext([]);
        pgContext.Model.GetEntityTypes().Should().HaveCountGreaterThanOrEqualTo(12);

        using var msContext = new OpenConnect.Samples.Persistence.SqlServer.ApplicationDbContextFactory().CreateDbContext([]);
        msContext.Model.GetEntityTypes().Should().HaveCountGreaterThanOrEqualTo(12);

        await using var sqliteConn = new SqliteConnection("DataSource=:memory:");
        await sqliteConn.OpenAsync();
        var sqliteOptions = new DbContextOptionsBuilder<OpenConnect.Samples.Persistence.Sqlite.ApplicationDbContext>()
            .UseSqlite(sqliteConn)
            .Options;
        await using var sqliteContext = new OpenConnect.Samples.Persistence.Sqlite.ApplicationDbContext(sqliteOptions);
        await sqliteContext.Database.EnsureCreatedAsync();
        sqliteContext.Model.GetEntityTypes().Should().HaveCountGreaterThanOrEqualTo(12);

        // Verify MAF Direct profile gate succeeds and unsupported profile fails closed
        OrchestrationProfileRouter.ValidateExecutionProfile("Direct");
        Action unsupportedProfile = () => OrchestrationProfileRouter.ValidateExecutionProfile("Harness");
        unsupportedProfile.Should().Throw<MafCapabilityUnavailableException>();
    }

    [Fact]
    public void EvidenceGenerator_ShouldEmitValidV1Schema_RedactSecrets_AndPreserveNotRunDefault()
    {
        var catalogue = EvidenceGenerator.BuildInitialCatalogue("run-test-001", "5b44e16");
        catalogue.Should().HaveCount(80);
        catalogue.Should().OnlyContain(r => r.Status == "NOT_RUN" && r.SchemaVersion == "openconnect.samples.workers.evidence/v1");

        var tempDir = Path.Combine(Path.GetTempPath(), "ocw-evidence-" + Guid.NewGuid().ToString("N"));
        try
        {
            EvidenceGenerator.WriteEvidenceBundle(tempDir, catalogue, "Bearer secretToken12345 and sk-TestSecretKey987654321");
            var resultJson = File.ReadAllText(Path.Combine(tempDir, "result.json"));
            var summaryMd = File.ReadAllText(Path.Combine(tempDir, "summary.md"));

            resultJson.Should().Contain("openconnect.samples.workers.evidence/v1");
            summaryMd.Should().NotContain("secretToken12345");
            summaryMd.Should().NotContain("sk-TestSecretKey987654321");
            summaryMd.Should().Contain("[REDACTED]");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void TraceabilityCsvAndSharedFixtures_ShouldCoverAllEightyAcceptanceCriteriaAndSevenFixtureCategories()
    {
        var repoRoot = FindRepoRoot();
        var csvPath = Path.Combine(repoRoot, "samples", "workers", "shared", "traceability.csv");
        File.Exists(csvPath).Should().BeTrue();

        var lines = File.ReadAllLines(csvPath).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        lines[0].Should().Be("sample_acceptance_id,source_requirement_ids,source_acceptance_ids,implementation_task_ids,test_names,ci_artifacts");
        lines.Should().HaveCount(81);

        for (var i = 1; i <= 80; i++)
        {
            var expectedId = $"WSE2E-AT-{i:D3}";
            var cols = lines[i].Split(',');
            cols.Should().HaveCount(6);
            cols[0].Should().Be(expectedId);
            cols.Should().OnlyContain(c => !string.IsNullOrWhiteSpace(c));
        }

        var fixturesRoot = Path.Combine(repoRoot, "samples", "workers", "shared", "fixtures");
        foreach (var category in new[] { "security", "translation", "audio", "image", "events", "contracts", "policy" })
        {
            var dir = Path.Combine(fixturesRoot, category);
            Directory.Exists(dir).Should().BeTrue($"fixture category '{category}' must exist");
            Directory.GetFiles(dir).Should().NotBeEmpty($"fixture category '{category}' must contain synthetic fixtures");
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OpenConnect.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
