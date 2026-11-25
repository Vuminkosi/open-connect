using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Messaging;
using OpenConnect.Core.Messaging;
using OpenConnect.Hosting.AspNetCore.Messaging;
using OpenConnect.IMessaging;
using OpenConnect.Persistence.EFCore.Messaging;
using OpenConnect.Samples.Blazor.Diagnostics;
using OpenConnect.Samples.Blazor.Models;

namespace OpenConnect.Samples.Blazor.Agents;

/// <summary>
/// Durable deadline monitor background service (with explicit <see cref="RunSweepNowAsync"/> for deterministic testing).
/// Detects expired deadlines on open/urgent tasks, ensures a human-inclusive AgentE2EE conversation,
/// sends a real encrypted message, dispatches to workers/imessaging if available (degraded/queued honestly if unavailable),
/// schedules a durable follow-up, and processes due follow-ups inside a scoped DI boundary.
/// </summary>
public sealed class TodoDeadlineMonitorService : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly DeviceId AgentVirtualDeviceId = new(Ulid.Parse("01ARZ3NDEKTSV4RRFFQ69G5FD0"));

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SqliteTodoStore _todoStore;
    private readonly TodoAcceptanceDiagnosticsService _diagnostics;
    private readonly IOptions<MessagingHostingOptions> _hostingOptions;
    private readonly IOptions<MessagingWorkerOptions> _workerOptions;
    private readonly IMessagingCryptoProvider _cryptoProvider;
    private readonly IMessagingRealtimeHub? _realtimeHub;
    private readonly IMessagingWorkerClient? _workerClient;
    private readonly ILogger<TodoDeadlineMonitorService> _logger;

    public TodoDeadlineMonitorService(
        IServiceScopeFactory scopeFactory,
        SqliteTodoStore todoStore,
        TodoAcceptanceDiagnosticsService diagnostics,
        IOptions<MessagingHostingOptions> hostingOptions,
        IOptions<MessagingWorkerOptions> workerOptions,
        IMessagingCryptoProvider cryptoProvider,
        ILogger<TodoDeadlineMonitorService> logger,
        IMessagingRealtimeHub? realtimeHub = null,
        IMessagingWorkerClient? workerClient = null)
    {
        _scopeFactory = scopeFactory;
        _todoStore = todoStore;
        _diagnostics = diagnostics;
        _hostingOptions = hostingOptions;
        _workerOptions = workerOptions;
        _cryptoProvider = cryptoProvider;
        _logger = logger;
        _realtimeHub = realtimeHub;
        _workerClient = workerClient;

        _diagnostics.RegisterSweepTrigger(RunSweepNowAsync);
    }

    public async Task<DeadlineSweepResult> RunSweepNowAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var notes = new List<string>();
        var alertsDispatched = 0;
        var followUpsScheduled = 0;
        var followUpsProcessed = 0;
        var workerStatus = "not_configured";

        if (!_hostingOptions.Value.Enabled)
        {
            return new DeadlineSweepResult(now, 0, 0, 0, 0, "messaging_disabled", ["IMessaging hosting is disabled."]);
        }

        var overdueTasks = await _todoStore.GetOverdueOperationalTasksAsync(now.UtcDateTime, cancellationToken).ConfigureAwait(false);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BlazorDbContext>();
        var coordinator = scope.ServiceProvider.GetRequiredService<IMessagingCoordinator>();
        var followUpCoordinator = scope.ServiceProvider.GetRequiredService<IFollowUpSchedulerCoordinator>();
        var followUpDispatcher = scope.ServiceProvider.GetService<IMessagingFollowUpDispatcher>();

        foreach (var task in overdueTasks)
        {
            if (string.IsNullOrWhiteSpace(task.TenantId) || string.IsNullOrWhiteSpace(task.AssignedPrincipalId))
                continue;

            TenantId tenantId;
            PrincipalId assigneeId;
            try
            {
                tenantId = new TenantId(Ulid.Parse(task.TenantId));
                assigneeId = new PrincipalId(Ulid.Parse(task.AssignedPrincipalId));
            }
            catch (FormatException)
            {
                continue;
            }

            if (TodoDirectorySeed.FindPrincipal(tenantId, assigneeId) is null)
                continue;

            try
            {
                var agentActor = new MessagingActor(
                    tenantId,
                    TodoDirectorySeed.ApplicationId,
                    ParticipantKind.Agent,
                    null,
                    TodoOperationalAgents.MonitoringAgent,
                    AgentVirtualDeviceId);

                var humanActor = new MessagingActor(
                    tenantId,
                    TodoDirectorySeed.ApplicationId,
                    ParticipantKind.Human,
                    assigneeId);

                var conversation = await EnsureAgentConversationAsync(db, coordinator, tenantId, agentActor, humanActor, cancellationToken).ConfigureAwait(false);
                await EnsureCryptoGroupAsync(tenantId, conversation.ConversationId, cancellationToken).ConfigureAwait(false);

                var alertPayload = JsonSerializer.Serialize(new
                {
                    kind = "deadline_expired_alert",
                    todoId = task.Id,
                    title = task.Title,
                    priority = task.Priority.ToString(),
                    dueDateUtc = task.DueDateUtc,
                    agentId = TodoOperationalAgents.TaskMonitoringAgentId,
                    message = $"Deadline expired for '{task.Title}'. Please reply with status or completion confirmation."
                }, JsonOptions);

                var aad = Encoding.UTF8.GetBytes($"{tenantId.Value}|{conversation.ConversationId.Value}|todo:{task.Id:N}");
                var envelope = await _cryptoProvider.EncryptAsync(
                    conversation.ConversationId,
                    AgentVirtualDeviceId,
                    Encoding.UTF8.GetBytes(alertPayload),
                    aad,
                    cancellationToken).ConfigureAwait(false);

                // Ensure envelope epoch aligns with conversation epoch
                if (envelope.CryptoEpoch != conversation.CryptoEpoch)
                    envelope = envelope with { CryptoEpoch = conversation.CryptoEpoch };

                var clientOpId = $"deadline-alert:todo:{task.Id:N}:rev:{task.Revision}";
                var commitResult = await coordinator.CommitMessageAsync(
                    agentActor,
                    conversation.ConversationId,
                    envelope,
                    clientOpId,
                    correlationId: $"todo:{task.Id:N}",
                    cancellationToken).ConfigureAwait(false);

                if (!commitResult.WasDeduplicated)
                {
                    alertsDispatched++;

                    // Realtime fanout to connected mobile/web devices
                    if (_realtimeHub is not null)
                    {
                        var wireEvent = new MessagingEventEnvelope(
                            SchemaVersion: 1,
                            EventId: $"msg-{commitResult.Message.MessageId.Value}",
                            Type: "messaging.message.committed",
                            TenantId: tenantId,
                            ConversationId: conversation.ConversationId,
                            Sequence: commitResult.Message.Sequence,
                            Cursor: commitResult.Message.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            OccurredAt: commitResult.Message.CreatedAt,
                            PayloadJson: JsonSerializer.Serialize(commitResult.Message, JsonOptions),
                            CorrelationId: commitResult.Message.CorrelationId);
                        await _realtimeHub.PublishToPrincipalAsync(tenantId, assigneeId, wireEvent, cancellationToken).ConfigureAwait(false);
                    }

                    // Dispatch to private Python workers/imessaging if configured (honestly mark degraded/queued if unavailable)
                    workerStatus = await TryDispatchToWorkerAsync(tenantId, conversation.ConversationId, commitResult.Message, task, cancellationToken).ConfigureAwait(false);

                    // Record push wake attempt honestly (never sends plaintext in push payload)
                    _diagnostics.RecordPushAttempt(
                        tenantId.Value.ToString(),
                        assigneeId.Value.ToString(),
                        null,
                        "wake-only",
                        workerStatus == "accepted" ? "dispatched" : "queued_local",
                        $"Opaque wake signal for conversation {conversation.ConversationId.Value} (no plaintext).");
                }

                // Schedule durable follow-up (idempotent per task + revision)
                var idempotencyKey = $"followup:todo:{task.Id:N}:rev:{task.Revision}";
                var causalEventId = $"deadline:todo:{task.Id:N}";
                var existingFollowUp = await db.Set<MessagingFollowUpEntity>()
                    .AsNoTracking()
                    .AnyAsync(f => f.TenantId == tenantId.Value.ToString() && f.IdempotencyKey == idempotencyKey, cancellationToken)
                    .ConfigureAwait(false);

                if (!existingFollowUp)
                {
                    await followUpCoordinator.ScheduleFollowUpAsync(
                        agentActor,
                        conversation.ConversationId,
                        assigneeId,
                        dueAtUtc: now.AddMinutes(5),
                        maxAttempts: 3,
                        idempotencyKey: idempotencyKey,
                        causalEventId: causalEventId,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    followUpsScheduled++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                notes.Add($"Task {task.Id}: {ex.Message}");
                _logger.LogWarning("Deadline sweep failed for task {TaskId}: {Error}", task.Id, ex.Message);
            }
        }

        // Also process any due follow-ups safely inside this DI scope
        if (followUpDispatcher is not null)
        {
            var opts = _hostingOptions.Value;
            var leaseOwner = string.IsNullOrWhiteSpace(opts.FollowUpLeaseOwner)
                ? $"todo-monitor-{Environment.MachineName}"
                : opts.FollowUpLeaseOwner;
            var claimed = await followUpCoordinator.ClaimDueBatchAsync(
                leaseOwner,
                TimeSpan.FromSeconds(30),
                10,
                cancellationToken).ConfigureAwait(false);

            foreach (var item in claimed)
            {
                try
                {
                    await followUpCoordinator.AuthorizeAndDispatchAsync(
                        item.TenantId,
                        item.FollowUpId,
                        leaseOwner,
                        item.LeaseFence,
                        (record, ct) => followUpDispatcher.DispatchAsync(record, ct),
                        cancellationToken).ConfigureAwait(false);
                    followUpsProcessed++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    notes.Add($"FollowUp {item.FollowUpId.Value}: {ex.Message}");
                }
            }
        }

        return new DeadlineSweepResult(
            SweptAtUtc: now,
            OverdueTasksFound: overdueTasks.Count,
            AlertsDispatched: alertsDispatched,
            FollowUpsScheduled: followUpsScheduled,
            FollowUpsProcessed: followUpsProcessed,
            WorkerStatus: workerStatus,
            Notes: notes);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial delay so startup migrations complete cleanly
        try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunSweepNowAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Background deadline sweep failed ({ExceptionType}).", ex.GetType().Name);
            }

            try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private static async Task<ConversationRecord> EnsureAgentConversationAsync(
        BlazorDbContext db,
        IMessagingCoordinator coordinator,
        TenantId tenantId,
        MessagingActor agentActor,
        MessagingActor humanActor,
        CancellationToken cancellationToken)
    {
        var tenantText = tenantId.Value.ToString();
        var principalText = humanActor.PrincipalId!.Value.Value.ToString();
        var agentText = agentActor.AgentId!.Value.Value;
        var activeStatus = ParticipantStatus.Active.ToString();
        var activeState = ConversationState.Active.ToString();
        var profileText = ConversationSecurityProfile.AgentE2EE.ToString();

        var existingConvId = await db.Set<MessagingConversationEntity>()
            .AsNoTracking()
            .Where(c => c.TenantId == tenantText
                && c.ApplicationId == TodoDirectorySeed.ApplicationId
                && c.State == activeState
                && c.SecurityProfile == profileText
                && c.Participants.Any(p => p.PrincipalId == principalText && p.Status == activeStatus)
                && c.Participants.Any(p => p.AgentId == agentText && p.Status == activeStatus))
            .Select(c => c.ConversationId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(existingConvId))
        {
            var store = await db.Set<MessagingConversationEntity>()
                .AsNoTracking()
                .Include(c => c.Participants)
                .SingleAsync(c => c.TenantId == tenantText && c.ConversationId == existingConvId, cancellationToken)
                .ConfigureAwait(false);

            return new ConversationRecord(
                tenantId,
                store.ApplicationId,
                new ConversationId(Ulid.Parse(store.ConversationId)),
                Enum.Parse<ConversationSecurityProfile>(store.SecurityProfile),
                Enum.Parse<ConversationState>(store.State),
                store.MembershipVersion,
                store.CryptoEpoch,
                store.NextSequence,
                new DateTimeOffset(DateTime.SpecifyKind(store.CreatedAtUtc, DateTimeKind.Utc)),
                store.Participants.Select(p => new ConversationParticipant(
                    new ConversationId(Ulid.Parse(p.ConversationId)),
                    Enum.Parse<ParticipantKind>(p.Kind),
                    string.IsNullOrWhiteSpace(p.PrincipalId) ? null : new PrincipalId(Ulid.Parse(p.PrincipalId)),
                    string.IsNullOrWhiteSpace(p.AgentId) ? null : new AgentId(p.AgentId),
                    Enum.Parse<ParticipantRole>(p.Role),
                    Enum.Parse<ParticipantStatus>(p.Status),
                    p.JoinVersion,
                    p.LeaveVersion,
                    new DateTimeOffset(DateTime.SpecifyKind(p.JoinedAtUtc, DateTimeKind.Utc)))).ToArray());
        }

        // Human-inclusive invariant: human + agent in AgentE2EE profile
        return await coordinator.CreateConversationAsync(
            agentActor,
            ConversationSecurityProfile.AgentE2EE,
            [humanActor],
            cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureCryptoGroupAsync(
        TenantId tenantId,
        ConversationId conversationId,
        CancellationToken cancellationToken)
    {
        try
        {
            var pkg = MessagingCryptoEnvelopeService.CreateKeyPackage(
                tenantId,
                AgentVirtualDeviceId,
                ownerAgentId: TodoOperationalAgents.MonitoringAgent);
            await _cryptoProvider.CreateGroupAsync(conversationId, [pkg], cancellationToken).ConfigureAwait(false);
        }
        catch (MessagingDomainException ex) when (string.Equals(ex.ErrorCode, ErrorCodes.Conflict, StringComparison.Ordinal))
        {
            // Group already initialized in memory for this conversation.
        }
    }

    private async Task<string> TryDispatchToWorkerAsync(
        TenantId tenantId,
        ConversationId conversationId,
        MessageRecord message,
        TodoItem task,
        CancellationToken cancellationToken)
    {
        if (_diagnostics.IsWorkerOutageInjected)
        {
            _diagnostics.RecordSecurityEvidence(
                category: "worker_dispatch",
                outcome: "degraded_queued",
                summary: $"Worker outage injected; encrypted message {message.MessageId.Value} durably committed to SQLite and queued.",
                tenantId: tenantId.Value.ToString());
            return "degraded_outage_injected";
        }

        var opts = _workerOptions.Value;
        if (!opts.Enabled || _workerClient is null ||
            (string.IsNullOrWhiteSpace(opts.WorkerEndpoint) && string.IsNullOrWhiteSpace(opts.UnixSocketPath)))
        {
            return "not_configured";
        }

        try
        {
            var now = DateTimeOffset.UtcNow;
            var command = new MessagingWorkerCommand(
                SchemaVersion: "openconnect.messaging.worker.v1",
                TaskId: $"todo-{task.Id:N}",
                Kind: "dispatch_encrypted_envelope",
                TenantId: tenantId.Value.ToString(),
                IssuedAt: now,
                ExpiresAt: now.AddMinutes(2),
                Nonce: Ulid.NewUlid().ToString(),
                PayloadJson: JsonSerializer.Serialize(new
                {
                    conversationId = conversationId.Value.ToString(),
                    messageId = message.MessageId.Value.ToString(),
                    sequence = message.Sequence,
                    protocol = message.Envelope.Protocol,
                    cryptoEpoch = message.Envelope.CryptoEpoch
                }, JsonOptions));

            var result = await _workerClient.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
            return result.Accepted ? "accepted" : $"rejected:{result.ErrorCode ?? "unknown"}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _diagnostics.RecordSecurityEvidence(
                category: "worker_dispatch",
                outcome: "degraded_queued",
                summary: $"Worker unreachable ({ex.GetType().Name}); encrypted message {message.MessageId.Value} remains durable in SQLite.",
                tenantId: tenantId.Value.ToString());
            return "unreachable_queued";
        }
    }
}
