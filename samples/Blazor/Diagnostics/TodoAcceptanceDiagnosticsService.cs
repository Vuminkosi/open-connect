using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Messaging;
using OpenConnect.Core.Messaging;
using OpenConnect.Hosting.AspNetCore.Messaging;
using OpenConnect.IMessaging;
using OpenConnect.Persistence.EFCore.Messaging;
using OpenConnect.Samples.Blazor.Agents;
using OpenConnect.Samples.Blazor.Models;

namespace OpenConnect.Samples.Blazor.Diagnostics;

/// <summary>
/// Development-only diagnostics, telemetry, and deterministic failure-injection service
/// for P0 End-to-End acceptance testing. Never fabricates readiness or security claims.
/// </summary>
public sealed class TodoAcceptanceDiagnosticsService(
    IServiceScopeFactory scopeFactory,
    SqliteTodoStore todoStore,
    IHostEnvironment environment,
    IOptions<MessagingHostingOptions> hostingOptions,
    IOptions<MessagingWorkerOptions> workerOptions,
    IMessagingCryptoProvider? cryptoProvider = null,
    IMessagingRealtimeHub? realtimeHub = null,
    IMessagingWorkerClient? workerClient = null)
{
    private const int MaxRingEntries = 100;
    private readonly ConcurrentQueue<PushAttemptDiagnostic> _pushAttempts = new();
    private readonly ConcurrentQueue<SecurityEvidenceEntry> _securityEvidence = new();
    private volatile bool _workerOutageInjected;
    private Func<CancellationToken, Task<DeadlineSweepResult>>? _sweepTrigger;

    public bool IsDevelopment => environment.IsDevelopment();
    public bool IsWorkerOutageInjected => _workerOutageInjected;

    internal void RegisterSweepTrigger(Func<CancellationToken, Task<DeadlineSweepResult>> trigger) =>
        _sweepTrigger = trigger;

    public void InjectWorkerOutage(bool enabled)
    {
        if (!environment.IsDevelopment())
            return;

        _workerOutageInjected = enabled;
        RecordSecurityEvidence(
            category: "failure_injection",
            outcome: enabled ? "degraded_injected" : "restored",
            summary: enabled
                ? "Simulated workers/imessaging outage enabled for acceptance testing."
                : "Simulated workers/imessaging outage cleared.");
    }

    public void RecordPushAttempt(
        string tenantId,
        string principalId,
        string? deviceId,
        string provider,
        string status,
        string summary)
    {
        _pushAttempts.Enqueue(new PushAttemptDiagnostic(
            TimestampUtc: DateTimeOffset.UtcNow,
            TenantId: tenantId,
            PrincipalId: principalId,
            DeviceId: deviceId,
            Provider: provider,
            Status: status,
            Summary: summary));
        while (_pushAttempts.Count > MaxRingEntries && _pushAttempts.TryDequeue(out _))
        { }
    }

    public void RecordSecurityEvidence(string category, string outcome, string summary, string? tenantId = null)
    {
        // Sanitized evidence only: never log raw secrets, tokens, or message plaintext.
        _securityEvidence.Enqueue(new SecurityEvidenceEntry(
            TimestampUtc: DateTimeOffset.UtcNow,
            Category: category,
            Outcome: outcome,
            TenantId: tenantId,
            Summary: summary));
        while (_securityEvidence.Count > MaxRingEntries && _securityEvidence.TryDequeue(out _))
        { }
    }

    public async Task<UrgentTaskCreationResult> CreateUrgentExpiringTaskAsync(
        string? tenantKey = null,
        string? assigneeKey = null,
        CancellationToken cancellationToken = default)
    {
        EnsureDevelopment();

        var assignee = TodoDirectorySeed.FindByKey(assigneeKey) ?? TodoDirectorySeed.DefaultPrincipal;
        var tenant = TodoDirectorySeed.FindTenantByKey(tenantKey) ?? TodoDirectorySeed.FindTenant(assignee.TenantId)!;
        if (assignee.TenantId != tenant.TenantId)
            assignee = TodoDirectorySeed.Principals.First(p => p.TenantId == tenant.TenantId);

        var dueAtUtc = DateTime.UtcNow.AddSeconds(-15); // Already expired for deterministic sweep testing
        var created = await todoStore.AddOperationalTaskAsync(
            tenant.TenantId,
            title: $"[URGENT] SLA Compliance Verification ({DateTime.UtcNow:HH:mm:ss} UTC)",
            description: "Deterministic acceptance task created with expired deadline to exercise operational agent E2EE alert & durable follow-up.",
            priority: TodoPriority.Urgent,
            dueDateUtc: dueAtUtc,
            assignedPrincipalId: assignee.PrincipalId,
            escalationPolicy: $"escalate:{assignee.SupervisorPrincipalId?.Value.ToString() ?? "none"}:15m",
            actorId: "acceptance-diagnostics",
            ct: cancellationToken).ConfigureAwait(false);

        RecordSecurityEvidence(
            category: "acceptance_control",
            outcome: "created",
            summary: $"Created urgent expiring task {created.Id} assigned to {assignee.Key} in {tenant.Key}.",
            tenantId: tenant.TenantId.Value.ToString());

        return new UrgentTaskCreationResult(
            Task: created,
            TenantKey: tenant.Key,
            AssigneeKey: assignee.Key,
            AssigneeDisplayName: assignee.DisplayName);
    }

    public async Task<DeadlineSweepResult> RunDeadlineSweepNowAsync(CancellationToken cancellationToken = default)
    {
        EnsureDevelopment();
        if (_sweepTrigger is null)
        {
            return new DeadlineSweepResult(
                SweptAtUtc: DateTimeOffset.UtcNow,
                OverdueTasksFound: 0,
                AlertsDispatched: 0,
                FollowUpsScheduled: 0,
                FollowUpsProcessed: 0,
                WorkerStatus: "unavailable",
                Notes: ["Deadline monitor service is not registered."]);
        }

        return await _sweepTrigger(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> RevokeDeviceAsync(
        TenantId tenantId,
        PrincipalId principalId,
        DeviceId deviceId,
        CancellationToken cancellationToken = default)
    {
        EnsureDevelopment();
        await using var scope = scopeFactory.CreateAsyncScope();
        var coordinator = scope.ServiceProvider.GetService<IDeviceEnrollmentCoordinator>();
        if (coordinator is null)
            return false;

        await coordinator.RevokeDeviceAsync(tenantId, hostingOptions.Value.ApplicationId, principalId, deviceId, cancellationToken).ConfigureAwait(false);
        if (realtimeHub is not null)
            await realtimeHub.CloseDeviceConnectionsAsync(tenantId, deviceId, "device_revoked", cancellationToken).ConfigureAwait(false);

        RecordSecurityEvidence(
            category: "device_revocation",
            outcome: "revoked",
            summary: $"Revoked device {deviceId.Value} for principal {principalId.Value} and terminated active WebSockets.",
            tenantId: tenantId.Value.ToString());

        return true;
    }

    public async Task<AcceptanceDiagnosticsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        if (!environment.IsDevelopment())
        {
            return new AcceptanceDiagnosticsSnapshot(
                IsDevelopment: false,
                GeneratedAtUtc: DateTimeOffset.UtcNow,
                MessagingEnabled: false,
                WorkerReadiness: new WorkerReadinessDiagnostic(false, false, false, "disabled", [], null, "Diagnostics are disabled outside Development."),
                CryptoProfile: BuildCryptoDiagnostic(),
                ActiveWebSocketConnections: 0,
                Tasks: [],
                FollowUps: [],
                Devices: [],
                Receipts: [],
                PushAttempts: [],
                SecurityEvidence: []);
        }

        var workerDiag = await CheckWorkerReadinessAsync(cancellationToken).ConfigureAwait(false);
        var tasks = await todoStore.GetAllAcrossTenantsForDiagnosticsAsync(cancellationToken).ConfigureAwait(false);

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BlazorDbContext>();

        var devices = await db.Set<MessagingDeviceEntity>()
            .AsNoTracking()
            .OrderByDescending(d => d.LastSeenAtUtc)
            .Take(50)
            .Select(d => new DeviceDiagnosticItem(
                d.TenantId,
                d.PrincipalId,
                d.DeviceId,
                d.DisplayName,
                d.Platform,
                d.Status,
                d.KeyFingerprint,
                new DateTimeOffset(DateTime.SpecifyKind(d.CreatedAtUtc, DateTimeKind.Utc)),
                new DateTimeOffset(DateTime.SpecifyKind(d.LastSeenAtUtc, DateTimeKind.Utc)),
                d.RevokedAtUtc.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(d.RevokedAtUtc.Value, DateTimeKind.Utc)) : null))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var followUps = await db.Set<MessagingFollowUpEntity>()
            .AsNoTracking()
            .OrderByDescending(f => f.DueAtUtc)
            .Take(50)
            .Select(f => new FollowUpDiagnosticItem(
                f.TenantId,
                f.FollowUpId,
                f.ConversationId,
                f.AgentId,
                f.RecipientPrincipalId,
                f.State,
                f.Attempt,
                f.MaxAttempts,
                new DateTimeOffset(DateTime.SpecifyKind(f.DueAtUtc, DateTimeKind.Utc)),
                f.IdempotencyKey,
                f.LastDispatchedAtUtc.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(f.LastDispatchedAtUtc.Value, DateTimeKind.Utc)) : null,
                f.CompletedAtUtc.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(f.CompletedAtUtc.Value, DateTimeKind.Utc)) : null))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var receipts = await db.Set<MessagingReceiptEntity>()
            .AsNoTracking()
            .OrderByDescending(r => r.RecordedAtUtc)
            .Take(50)
            .Select(r => new ReceiptDiagnosticItem(
                r.TenantId,
                r.ConversationId,
                r.MessageId,
                r.PrincipalId,
                r.DeviceId,
                r.Kind,
                r.Sequence,
                new DateTimeOffset(DateTime.SpecifyKind(r.RecordedAtUtc, DateTimeKind.Utc)),
                r.FailureReason))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new AcceptanceDiagnosticsSnapshot(
            IsDevelopment: true,
            GeneratedAtUtc: DateTimeOffset.UtcNow,
            MessagingEnabled: hostingOptions.Value.Enabled,
            WorkerReadiness: workerDiag,
            CryptoProfile: BuildCryptoDiagnostic(),
            ActiveWebSocketConnections: realtimeHub?.ActiveConnectionCount ?? 0,
            Tasks: tasks,
            FollowUps: followUps,
            Devices: devices,
            Receipts: receipts,
            PushAttempts: _pushAttempts.Reverse().ToArray(),
            SecurityEvidence: _securityEvidence.Reverse().ToArray());
    }

    private async Task<WorkerReadinessDiagnostic> CheckWorkerReadinessAsync(CancellationToken cancellationToken)
    {
        var opts = workerOptions.Value;
        var configured = opts.Enabled && (!string.IsNullOrWhiteSpace(opts.WorkerEndpoint) || !string.IsNullOrWhiteSpace(opts.UnixSocketPath));

        if (_workerOutageInjected)
        {
            return new WorkerReadinessDiagnostic(
                Configured: configured,
                IsReady: false,
                OutageInjected: true,
                Status: "degraded_outage_injected",
                VerifiedCapabilities: [],
                Endpoint: opts.WorkerEndpoint ?? opts.UnixSocketPath,
                Detail: "Simulated worker outage is active; outbound worker dispatch is queued/degraded honestly.");
        }

        if (!configured || workerClient is null)
        {
            return new WorkerReadinessDiagnostic(
                Configured: false,
                IsReady: false,
                OutageInjected: false,
                Status: "not_configured",
                VerifiedCapabilities: [],
                Endpoint: opts.WorkerEndpoint ?? opts.UnixSocketPath,
                Detail: "Private workers/imessaging endpoint is not configured; local E2EE messaging & SQLite persistence remain active.");
        }

        try
        {
            var readiness = await workerClient.CheckReadinessAsync(cancellationToken).ConfigureAwait(false);
            return new WorkerReadinessDiagnostic(
                Configured: true,
                IsReady: readiness.IsReady,
                OutageInjected: false,
                Status: readiness.IsReady ? "ready" : "degraded",
                VerifiedCapabilities: readiness.VerifiedCapabilities,
                Endpoint: opts.WorkerEndpoint ?? opts.UnixSocketPath,
                Detail: readiness.IsReady
                    ? $"Verified contract {readiness.ContractVersion ?? "v1"}."
                    : "Worker responded but did not report ready.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new WorkerReadinessDiagnostic(
                Configured: true,
                IsReady: false,
                OutageInjected: false,
                Status: "unreachable",
                VerifiedCapabilities: [],
                Endpoint: opts.WorkerEndpoint ?? opts.UnixSocketPath,
                Detail: $"Worker unreachable ({ex.GetType().Name}); degraded/queued honestly.");
        }
    }

    private CryptoProfileDiagnostic BuildCryptoDiagnostic() =>
        new(
            ProtocolName: cryptoProvider?.ProtocolName ?? MessagingCryptoEnvelopeService.CanonicalProtocolName,
            RequireEncryption: hostingOptions.Value.RequireEncryption,
            AllowUnencryptedFallback: hostingOptions.Value.AllowUnencryptedFallback,
            FullRfc9420MlsInteropComplete: false,
            HonestDisclosure: "ECDH P-256 + HKDF-SHA256 + AES-256-GCM authenticated envelope with IMessagingCryptoProvider MLS extension seam. Full RFC 9420 MLS wire interoperability is NOT yet claimed.");

    private void EnsureDevelopment()
    {
        if (!environment.IsDevelopment())
            throw new InvalidOperationException("Acceptance diagnostics controls are strictly Development-only.");
    }
}

public static class TodoAcceptanceDiagnosticsEndpoints
{
    public static IEndpointRouteBuilder MapTodoAcceptanceDiagnostics(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/__diagnostics/acceptance");

        group.MapGet("/snapshot", async (TodoAcceptanceDiagnosticsService service, CancellationToken ct) =>
            Results.Ok(await service.GetSnapshotAsync(ct).ConfigureAwait(false)));

        group.MapPost("/urgent-task", async (
            UrgentTaskCreateRequest? request,
            TodoAcceptanceDiagnosticsService service,
            CancellationToken ct) =>
            Results.Ok(await service.CreateUrgentExpiringTaskAsync(request?.TenantKey, request?.AssigneeKey, ct).ConfigureAwait(false)));

        group.MapPost("/sweep", async (TodoAcceptanceDiagnosticsService service, CancellationToken ct) =>
            Results.Ok(await service.RunDeadlineSweepNowAsync(ct).ConfigureAwait(false)));

        group.MapPost("/worker-outage", (WorkerOutageToggleRequest request, TodoAcceptanceDiagnosticsService service) =>
        {
            service.InjectWorkerOutage(request.Enabled);
            return Results.Ok(new { outageInjected = service.IsWorkerOutageInjected });
        });

        group.MapPost("/persona/{key}", (string key, HttpContext context) =>
        {
            var found = TodoDirectorySeed.FindByKey(key);
            if (found is null)
                return Results.NotFound(new { error = "Unknown development persona key." });

            context.Response.Cookies.Append("oc_dev_persona", found.Key, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                IsEssential = true,
                Path = "/"
            });
            return Results.Ok(new
            {
                found.Key,
                found.TenantKey,
                TenantId = found.TenantId.Value.ToString(),
                PrincipalId = found.PrincipalId.Value.ToString(),
                found.DisplayName,
                found.Role
            });
        });

        return endpoints;
    }
}

public sealed record UrgentTaskCreateRequest(string? TenantKey = null, string? AssigneeKey = null);
public sealed record WorkerOutageToggleRequest(bool Enabled);

public sealed record AcceptanceDiagnosticsSnapshot(
    bool IsDevelopment,
    DateTimeOffset GeneratedAtUtc,
    bool MessagingEnabled,
    WorkerReadinessDiagnostic WorkerReadiness,
    CryptoProfileDiagnostic CryptoProfile,
    int ActiveWebSocketConnections,
    IReadOnlyList<TodoItem> Tasks,
    IReadOnlyList<FollowUpDiagnosticItem> FollowUps,
    IReadOnlyList<DeviceDiagnosticItem> Devices,
    IReadOnlyList<ReceiptDiagnosticItem> Receipts,
    IReadOnlyList<PushAttemptDiagnostic> PushAttempts,
    IReadOnlyList<SecurityEvidenceEntry> SecurityEvidence);

public sealed record WorkerReadinessDiagnostic(
    bool Configured,
    bool IsReady,
    bool OutageInjected,
    string Status,
    IReadOnlyList<string> VerifiedCapabilities,
    string? Endpoint,
    string Detail);

public sealed record CryptoProfileDiagnostic(
    string ProtocolName,
    bool RequireEncryption,
    bool AllowUnencryptedFallback,
    bool FullRfc9420MlsInteropComplete,
    string HonestDisclosure);

public sealed record DeviceDiagnosticItem(
    string TenantId,
    string PrincipalId,
    string DeviceId,
    string DisplayName,
    string Platform,
    string Status,
    string KeyFingerprint,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastSeenAtUtc,
    DateTimeOffset? RevokedAtUtc);

public sealed record FollowUpDiagnosticItem(
    string TenantId,
    string FollowUpId,
    string ConversationId,
    string AgentId,
    string RecipientPrincipalId,
    string State,
    int Attempt,
    int MaxAttempts,
    DateTimeOffset DueAtUtc,
    string IdempotencyKey,
    DateTimeOffset? LastDispatchedAtUtc,
    DateTimeOffset? CompletedAtUtc);

public sealed record ReceiptDiagnosticItem(
    string TenantId,
    string ConversationId,
    string MessageId,
    string PrincipalId,
    string DeviceId,
    string Kind,
    long Sequence,
    DateTimeOffset RecordedAtUtc,
    string? FailureReason);

public sealed record PushAttemptDiagnostic(
    DateTimeOffset TimestampUtc,
    string TenantId,
    string PrincipalId,
    string? DeviceId,
    string Provider,
    string Status,
    string Summary);

public sealed record SecurityEvidenceEntry(
    DateTimeOffset TimestampUtc,
    string Category,
    string Outcome,
    string? TenantId,
    string Summary);

public sealed record UrgentTaskCreationResult(
    TodoItem Task,
    string TenantKey,
    string AssigneeKey,
    string AssigneeDisplayName);

public sealed record DeadlineSweepResult(
    DateTimeOffset SweptAtUtc,
    int OverdueTasksFound,
    int AlertsDispatched,
    int FollowUpsScheduled,
    int FollowUpsProcessed,
    string WorkerStatus,
    IReadOnlyList<string> Notes);
