using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Core.Capabilities;
using OpenConnect.Persistence.EFCore.Model;
using OpenConnect.Privacy;
using OpenConnect.Privacy.Policy;
using OpenConnect.Privacy.Tokenization;
using OpenConnect.Samples.Workers.Contracts;

namespace OpenConnect.Samples.Workers.Host;

public sealed class SampleHostDbContext : DbContext
{
    public SampleHostDbContext(DbContextOptions<SampleHostDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyOpenConnectModel(new OpenConnectModelOptions
        {
            UseSqlitePrefix = true,
            SqliteTablePrefix = "oc_"
        });
    }
}

public sealed class FakeExternalModelSink
{
    private readonly ConcurrentQueue<string> _receivedPayloads = new();
    private long _receivedBytes;

    public long ReceivedBytes => Interlocked.Read(ref _receivedBytes);
    public IReadOnlyCollection<string> ReceivedPayloads => _receivedPayloads.ToArray();

    public void RecordOutbound(string sanitizedPayload)
    {
        ArgumentNullException.ThrowIfNull(sanitizedPayload);
        Interlocked.Add(ref _receivedBytes, Encoding.UTF8.GetByteCount(sanitizedPayload));
        _receivedPayloads.Enqueue(sanitizedPayload);
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _receivedBytes, 0);
        while (_receivedPayloads.TryDequeue(out _)) { }
    }
}

public sealed partial class SampleRulesSecurityDetector : ISecurityDetector
{
    private readonly ITokenVault _vault;
    private readonly ISet<SecurityDetectorKind> _enabledDetectors;
    private readonly ISet<SecurityCoverage> _requiredCoverages;

    public SampleRulesSecurityDetector(
        ITokenVault vault,
        IEnumerable<SecurityDetectorKind>? enabledDetectors = null,
        IEnumerable<SecurityCoverage>? requiredCoverages = null)
    {
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _enabledDetectors = new HashSet<SecurityDetectorKind>(enabledDetectors ?? [SecurityDetectorKind.Rules]);
        _requiredCoverages = new HashSet<SecurityCoverage>(requiredCoverages ?? [SecurityCoverage.StructuredPii]);
    }

    public bool IsDetectorEnabled(SecurityDetectorKind kind) => _enabledDetectors.Contains(kind);

    [GeneratedRegex(@"[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}")]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"\b(?:\+?1[-.\s]?)?\(?\d{3}\)?[-.\s]?\d{3}[-.\s]?\d{4}\b")]
    private static partial Regex PhoneRegex();

    [GeneratedRegex(@"\b(?:sk-[A-Za-z0-9]{12,}|OC_SECRET_[A-Za-z0-9_]+)\b")]
    private static partial Regex SecretRegex();

    public async ValueTask<DetectorResult> DetectAndSanitizeAsync(
        string content,
        EgressContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(context);

        if (!_enabledDetectors.Contains(SecurityDetectorKind.Rules))
        {
            return new DetectorResult(false, string.Empty, Array.Empty<EntitySpan>());
        }

        // Fail closed when policy requires semantic/image/injection coverage whose detector is disabled
        if (_requiredCoverages.Contains(SecurityCoverage.SemanticPii) && !_enabledDetectors.Contains(SecurityDetectorKind.GlinerPii))
            return new DetectorResult(false, string.Empty, Array.Empty<EntitySpan>());
        if (_requiredCoverages.Contains(SecurityCoverage.PromptInjection) && !_enabledDetectors.Contains(SecurityDetectorKind.DebertaInjection))
            return new DetectorResult(false, string.Empty, Array.Empty<EntitySpan>());
        if (_requiredCoverages.Contains(SecurityCoverage.ContentSafety) && !_enabledDetectors.Contains(SecurityDetectorKind.Qwen3Guard))
            return new DetectorResult(false, string.Empty, Array.Empty<EntitySpan>());
        if (_requiredCoverages.Contains(SecurityCoverage.ImageText) && !_enabledDetectors.Contains(SecurityDetectorKind.PaddleOcr))
            return new DetectorResult(false, string.Empty, Array.Empty<EntitySpan>());
        if (_requiredCoverages.Contains(SecurityCoverage.FaceDetection) && !_enabledDetectors.Contains(SecurityDetectorKind.YuNet))
            return new DetectorResult(false, string.Empty, Array.Empty<EntitySpan>());

        var spans = new List<EntitySpan>();
        foreach (Match m in EmailRegex().Matches(content))
            spans.Add(new EntitySpan(m.Index, m.Length, "email"));
        foreach (Match m in PhoneRegex().Matches(content))
            spans.Add(new EntitySpan(m.Index, m.Length, "phone"));
        foreach (Match m in SecretRegex().Matches(content))
            spans.Add(new EntitySpan(m.Index, m.Length, "secret"));

        spans = spans
            .OrderBy(s => s.Start)
            .ThenByDescending(s => s.Length)
            .ToList();

        // Deduplicate overlapping spans (keep priority longest/earliest)
        var nonOverlapping = new List<EntitySpan>();
        var lastEnd = -1;
        foreach (var s in spans)
        {
            if (s.Start >= lastEnd)
            {
                nonOverlapping.Add(s);
                lastEnd = s.Start + s.Length;
            }
        }

        if (!WorkerContractAssertions.ValidateDetectorSpans(content, nonOverlapping, out _))
        {
            return new DetectorResult(false, string.Empty, Array.Empty<EntitySpan>());
        }

        var scope = new PrivacyScope(context.Tenant, context.Principal, context.Session, "external_egress");
        var sb = new StringBuilder();
        var cursor = 0;
        foreach (var span in nonOverlapping)
        {
            if (span.Start > cursor)
            {
                sb.Append(content.AsSpan(cursor, span.Start - cursor));
            }
            var rawValue = content.Substring(span.Start, span.Length);
            var token = await _vault.StoreAsync(rawValue, span.EntityType, scope, TimeSpan.FromMinutes(30), cancellationToken);
            sb.Append(token);
            cursor = span.Start + span.Length;
        }
        if (cursor < content.Length)
        {
            sb.Append(content.AsSpan(cursor));
        }

        return new DetectorResult(true, sb.ToString(), nonOverlapping);
    }
}

public sealed class SampleGatewayCoordinator
{
    public ICapabilityRegistry CapabilityRegistry { get; }
    public ITokenVault TokenVault { get; }
    public FakeExternalModelSink ExternalSink { get; }
    public SampleRulesSecurityDetector Detector { get; }
    public PrivacyFirewall Firewall { get; }

    public SampleGatewayCoordinator(
        IEnumerable<SecurityDetectorKind>? enabledDetectors = null,
        IEnumerable<SecurityCoverage>? requiredCoverages = null,
        FirewallMode firewallMode = FirewallMode.StrictLocal)
    {
        CapabilityRegistry = new CapabilityRegistry();
        TokenVault = new TokenVault(RandomNumberGenerator.GetBytes(32));
        ExternalSink = new FakeExternalModelSink();
        Detector = new SampleRulesSecurityDetector(TokenVault, enabledDetectors, requiredCoverages);
        var policyEngine = new PolicyEngine(Detector);
        var options = Options.Create(new PrivacyOptions
        {
            Enabled = firewallMode != FirewallMode.Disabled,
            Mode = firewallMode,
            DefaultTokenTtl = TimeSpan.FromMinutes(30)
        });
        Firewall = new PrivacyFirewall(options, policyEngine);

        if (Detector.IsDetectorEnabled(SecurityDetectorKind.Rules))
        {
            CapabilityRegistry.Register(new CapabilityDescriptor(
                new CapabilityId("worker.security.rules"),
                CapabilityKind.Security,
                DetectorState.Ready));
        }
    }

    public static void ValidateHostingExclusivity(
        bool embeddedOperationalEnabled,
        bool standaloneOperationalEnabled,
        bool embeddedOutboxEnabled,
        bool standaloneOutboxEnabled)
    {
        if (embeddedOperationalEnabled && standaloneOperationalEnabled)
        {
            throw new InvalidOperationException("worker_hosting_mode_conflict: OperationalAgents cannot run in both Embedded and Standalone modes simultaneously.");
        }
        if (embeddedOutboxEnabled && standaloneOutboxEnabled)
        {
            throw new InvalidOperationException("worker_hosting_mode_conflict: Outbox cannot run in both Embedded and Standalone modes simultaneously.");
        }
    }

    public async Task<EgressDecision> EvaluateAndSendToExternalSinkAsync(
        string content,
        TenantId tenant,
        PrincipalId principal,
        SessionId session,
        CancellationToken cancellationToken = default)
    {
        var context = new EgressContext(tenant, principal, session, EgressDestination.ExternalModel);
        var decision = await Firewall.EvaluateAsync(content, context, cancellationToken);
        if (decision.Allowed && decision.SanitizedContent is not null)
        {
            ExternalSink.RecordOutbound(decision.SanitizedContent);
        }
        return decision;
    }

    public ValueTask<string?> RestoreTokenAsync(
        string token,
        TenantId tenant,
        PrincipalId principal,
        SessionId session,
        AgentIdentityMode identityMode,
        CancellationToken cancellationToken = default)
    {
        // Operational workload identity cannot restore user interactive tokens (§5.1)
        if (identityMode == AgentIdentityMode.WorkloadIdentity)
        {
            return ValueTask.FromResult<string?>(null);
        }

        var scope = new PrivacyScope(tenant, principal, session, "external_egress");
        return TokenVault.ResolveAsync(token, scope, cancellationToken);
    }
}
