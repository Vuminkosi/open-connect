using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Persistence;

namespace OpenConnect.Agents.Execution;

/// <summary>Runs a lead agent across turns and records real delegated agent executions.</summary>
public sealed class MafConversationRunner
{
    private readonly MafRealAgentFactory _factory;
    private readonly AgentId _leadAgentId;
    private readonly SessionTurnConcurrency _concurrency;
    private readonly RunBudgetLedger? _runLedger;
    private readonly IRateLimitGate? _rateLimitGate;
    private readonly List<ChatMessage> _history = [];
    private AIAgent? _leadAgent;
    private AgentSession? _leadSession;
    private readonly List<ExecutionTraceRecord> _trace = [];
    private readonly Dictionary<(AgentId Child, string Key), AgentSession> _childSessions = [];
    private readonly Dictionary<(string AgentId, string Key), int> _childHistoryCounts = [];
    private long _turnSequence;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _turnGate = new(1, 1);
    private bool? _durableMode;
    private NativeAgentSessionBinding? _durableBinding;
    private bool _unusable;

    /// <summary>Creates a conversation runner for the specified lead agent.</summary>
    /// <param name="factory">Factory used to create the lead agent.</param>
    /// <param name="leadAgentId">Identifier of the lead agent.</param>
    /// <param name="concurrency">Policy for overlapping turns. Queue currently has the same FIFO gate-wait behavior as Serialize; bounded-queue semantics are not specified.</param>
    /// <param name="runLedger">Optional shared run-wide budget ledger. Each invocation reserves once; tokens are estimated from prompt UTF-8 bytes, not response tokens.</param>
    /// <param name="rateLimitGate">Optional tenant/application rate gate; when absent, rate limiting is skipped. When present, turns and delegated children are each gated exactly once with the attributed tenant before any reservation; unattributed calls deny fail-closed.</param>
    /// <param name="pinnedCatalogRevision">Optional pinned catalog revision for the run; when specified, resume attempts with incompatible revisions fail closed.</param>
    public MafConversationRunner(MafRealAgentFactory factory, AgentId leadAgentId,
        SessionTurnConcurrency concurrency = SessionTurnConcurrency.Serialize, RunBudgetLedger? runLedger = null,
        IRateLimitGate? rateLimitGate = null, string? pinnedCatalogRevision = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _leadAgentId = leadAgentId;
        if (!Enum.IsDefined(concurrency)) throw new ArgumentOutOfRangeException(nameof(concurrency));
        _concurrency = concurrency;
        _runLedger = runLedger;
        _rateLimitGate = rateLimitGate;
        PinnedCatalogRevision = pinnedCatalogRevision;
    }

    /// <summary>Gets the pinned catalog revision for this runner, if any.</summary>
    public string? PinnedCatalogRevision { get; }

    /// <summary>Returns a snapshot of the conversation's user and assistant messages.</summary>
    public IReadOnlyList<ChatMessage> History
    {
        get { lock (_sync) return _history.ToArray(); }
    }

    /// <summary>Returns trace records for actual calls made by this runner.</summary>
    public IReadOnlyList<ExecutionTraceRecord> GetTrace()
    {
        lock (_sync) return _trace.ToArray();
    }

    /// <summary>Runs a budgeted turn using conversation history and appends the actual response.</summary>
    /// <remarks>When configured, the runner reserves once before invocation using prompt UTF-8 bytes as an input-token estimate; response tokens are unknown upfront.</remarks>
    /// <param name="prompt">Prompt supplied for this turn; UTF-8 byte count is used as a conservative input-token estimate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="tenant">Tenant authority scope the turn is attributed to. When a rate gate is configured, the gate is evaluated exactly once for this turn before any budget reservation or MAF invocation; a missing tenant denies fail-closed.</param>
    public async Task<AgentResponse> TurnAsync(string prompt, CancellationToken cancellationToken = default, string? tenant = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        cancellationToken.ThrowIfCancellationRequested();
        PinMode(false, null);
        if (_concurrency == SessionTurnConcurrency.RejectConflict)
        {
            if (!_turnGate.Wait(0)) throw new InvalidOperationException("A conversation turn is already active; concurrent turns are not allowed.");
        }
        else
        {
            await _turnGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        var promptIndex = -1;
        Guid executionId = Guid.Empty;
        try
        {
            // Fail before invoking MAF or recording trace/history when no monotonic sequence remains.
            lock (_sync) _ = checked(_turnSequence + 1);
            ThrowIfRateLimited(tenant, _leadAgentId.Value, Guid.NewGuid().ToString("N"), cancellationToken);
            ReserveModelCall(prompt);
            var agent = _leadAgent ?? _factory.CreateAgent(_leadAgentId);
            AgentSession candidateSession;
            if (_leadSession is null)
            {
                candidateSession = await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // The serialized element is an ephemeral in-memory snapshot. Clone it so
                // its lifetime is independent of any provider-owned JsonDocument.
                var serializedSession = await agent.SerializeSessionAsync(
                    _leadSession, cancellationToken: cancellationToken).ConfigureAwait(false);
                candidateSession = await agent.DeserializeSessionAsync(
                    serializedSession.Clone(), cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            executionId = Guid.NewGuid();
            AddTrace(new ExecutionTraceRecord(executionId, null, _leadAgentId.Value, "lead", DateTimeOffset.UtcNow, null));
            lock (_sync)
            {
                promptIndex = _history.Count;
                _history.Add(new ChatMessage(ChatRole.User, prompt));
            }
            var response = await agent.RunAsync(prompt, candidateSession, cancellationToken: cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _history.AddRange(response.Messages);
                _turnSequence = checked(_turnSequence + 1);
                _leadAgent = agent;
                _leadSession = candidateSession;
            }
            CompleteTrace(executionId);
            return response;
        }
        catch
        {
            lock (_sync)
            {
                if (promptIndex >= 0 && promptIndex < _history.Count && _history[promptIndex].Role == ChatRole.User && _history[promptIndex].Text == prompt)
                    _history.RemoveAt(promptIndex);
            }
            if (executionId != Guid.Empty) CompleteTrace(executionId);
            throw;
        }
        finally
        {
            _turnGate.Release();
        }
    }

    /// <summary>Runs the requested child agent, reserves its run-wide model budget once, and records its real execution beneath the supplied lead execution.</summary>
    /// <remarks>Prompt UTF-8 bytes provide a conservative input-token estimate; response tokens are unknown upfront.</remarks>
    /// <param name="parentExecutionId">Parent execution identifier.</param>
    /// <param name="childAgentId">Child agent identifier.</param>
    /// <param name="prompt">Prompt supplied to the child; UTF-8 byte count is used as a conservative input-token estimate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="childSessionKey">Optional host-assigned key for continuing this child agent's session.</param>
    /// <param name="tenant">Tenant authority scope the delegation is attributed to. When a rate gate is configured, the gate is evaluated exactly once for this delegation before any budget reservation or MAF invocation; a missing tenant denies fail-closed.</param>
    /// <remarks>The host owns key assignment. A key must not be shared across concurrent branches. The map is bounded by this runner's lifetime and is not durable across restarts; durable session ownership belongs to OpenConnect.Sessions. No trace record exists for work that never began: session creation failure creates no record, and every recorded trace is completed by its owning finally.</remarks>
    /// <returns>The child agent response.</returns>
    /// <exception cref="InvalidOperationException">The configured run budget is exhausted.</exception>
    public async Task<AgentResponse> DelegateAsync(Guid parentExecutionId, AgentId childAgentId, string prompt,
        CancellationToken cancellationToken = default, string? childSessionKey = null, string? tenant = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrEmpty(childAgentId.Value);
        if (!string.IsNullOrEmpty(childSessionKey) && string.IsNullOrWhiteSpace(childSessionKey))
            throw new ArgumentException("Session key cannot be whitespace.", nameof(childSessionKey));
        lock (_sync)
        {
            if (_unusable)
                throw new InvalidOperationException("This conversation runner is unusable after native session ownership cleanup failed.");
        }
        ThrowIfRateLimited(tenant, childAgentId.Value, Guid.NewGuid().ToString("N"), cancellationToken);
        ReserveModelCall(prompt);
        var agent = _factory.CreateAgent(childAgentId);
        if (string.IsNullOrEmpty(childSessionKey))
        {
            var executionId = Guid.NewGuid();
            AddTrace(new ExecutionTraceRecord(executionId, parentExecutionId, childAgentId.Value, "agent", DateTimeOffset.UtcNow, null));
            return await RunChildAsync(agent, executionId, prompt, cancellationToken).ConfigureAwait(false);
        }

        AgentSession? session;
        lock (_sync) _childSessions.TryGetValue((childAgentId, childSessionKey), out session);
        if (session is null)
        {
            var createdSession = await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                if (!_childSessions.TryGetValue((childAgentId, childSessionKey), out session))
                {
                    session = createdSession;
                    _childSessions.Add((childAgentId, childSessionKey), session);
                }
            }
        }

        var keyedExecutionId = Guid.NewGuid();
        AddTrace(new ExecutionTraceRecord(keyedExecutionId, parentExecutionId, childAgentId.Value, "agent", DateTimeOffset.UtcNow, null));
        try
        {
            var response = await agent.RunAsync(prompt, session, cancellationToken: cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                var countKey = (childAgentId.Value, childSessionKey);
                _childHistoryCounts.TryGetValue(countKey, out var count);
                _childHistoryCounts[countKey] = checked(count + 1);
            }
            return response;
        }
        finally
        {
            CompleteTrace(keyedExecutionId);
        }
    }

    /// <summary>Indicates whether this runner can safely accept additional turns.</summary>
    /// <remarks>False means native-session ownership cleanup failed and the runner must be discarded.</remarks>
    public bool IsUsable
    {
        get { lock (_sync) return !_unusable; }
    }

    /// <summary>Runs one authorized native-session turn and publishes local state only after durable CAS succeeds.</summary>
    /// <remarks>Authentication, authorization and trusted binding selection belong to the host. History is only this runner's projection and is not restored as a transcript. This runner cannot prove distributed effect fencing; effect implementations need their own fencing or idempotency.</remarks>
    public async Task<AgentResponse> TurnDurableAsync(string prompt, AgentExecutionContext executionContext,
        IsExecutionRevoked isExecutionRevoked, NativeSessionTurnPersistenceContext persistenceContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentNullException.ThrowIfNull(executionContext);
        ArgumentNullException.ThrowIfNull(isExecutionRevoked);
        ArgumentNullException.ThrowIfNull(persistenceContext);
        var binding = persistenceContext.Binding;
        ValidateExecutionBinding(executionContext, binding);
        if (PinnedCatalogRevision is not null &&
            !StringComparer.Ordinal.Equals(binding.DefinitionRevision, PinnedCatalogRevision))
        {
            throw new InvalidOperationException(
                $"The native session binding definition revision '{binding.DefinitionRevision}' does not match the runner's pinned catalog revision '{PinnedCatalogRevision}'.");
        }
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, executionContext.CancellationToken);
        var requestToken = requestCancellation.Token;
        await EnsureAuthorizedAsync(executionContext, isExecutionRevoked, requestToken).ConfigureAwait(false);
        PinMode(true, binding);
        var ownership = await persistenceContext.TurnGate.TryAcquireAsync(binding, executionContext, requestToken)
            .ConfigureAwait(false);
        if (ownership is null)
            throw new InvalidOperationException("The native session is busy or authorization was rejected.");

        using (var turnCancellation = CancellationTokenSource.CreateLinkedTokenSource(requestToken, ownership.OwnershipLost))
        {
            var ct = turnCancellation.Token;
            var promptIndex = -1;
            var executionId = Guid.Empty;
            var committed = false;
            Exception? turnFailure = null;
            try
            {
                await ValidateOwnershipAndAuthorizationAsync(ownership, executionContext, isExecutionRevoked, ct).ConfigureAwait(false);
                lock (_sync) _ = checked(_turnSequence + 1);
                var agent = _factory.CreateAgent(_leadAgentId);
                var loaded = await persistenceContext.Persistence.LoadAsync(agent, binding, ct).ConfigureAwait(false);
                await ValidateOwnershipAndAuthorizationAsync(ownership, executionContext, isExecutionRevoked, ct).ConfigureAwait(false);
                var candidateSession = loaded?.Session ?? await agent.CreateSessionAsync(ct).ConfigureAwait(false);
                var expectedVersion = loaded?.Version ?? 0;
                ThrowIfRateLimited(executionContext.TenantScope, _leadAgentId.Value, executionContext.AgentExecutionId.Value.ToString(), ct);
                ReserveModelCall(prompt);
                await ValidateOwnershipAndAuthorizationAsync(ownership, executionContext, isExecutionRevoked, ct).ConfigureAwait(false);
                executionId = Guid.NewGuid();
                AddTrace(new ExecutionTraceRecord(executionId, null, _leadAgentId.Value, "lead", DateTimeOffset.UtcNow, null));
                lock (_sync)
                {
                    promptIndex = _history.Count;
                    _history.Add(new ChatMessage(ChatRole.User, prompt));
                }
                var response = await agent.RunAsync(prompt, candidateSession, cancellationToken: ct).ConfigureAwait(false);
                await ValidateOwnershipAndAuthorizationAsync(ownership, executionContext, isExecutionRevoked, ct).ConfigureAwait(false);
                var save = await persistenceContext.Persistence.SaveAsync(agent, candidateSession, binding, expectedVersion, ct).ConfigureAwait(false);
                if (save == NativeAgentSessionSaveOutcome.Conflict)
                    throw new InvalidOperationException("The native session changed during this turn; no result was committed.");
                if (save != NativeAgentSessionSaveOutcome.Saved)
                    throw new InvalidOperationException("The native session save returned an unsupported outcome.");
                lock (_sync)
                {
                    _history.AddRange(response.Messages);
                    _turnSequence = checked(_turnSequence + 1);
                    _leadAgent = agent;
                    _leadSession = candidateSession;
                }
                committed = true;
                CompleteTrace(executionId);
                return response;
            }
            catch (Exception exception)
            {
                turnFailure = exception;
                lock (_sync)
                {
                    if (promptIndex >= 0 && promptIndex < _history.Count && _history[promptIndex].Role == ChatRole.User && _history[promptIndex].Text == prompt)
                        _history.RemoveAt(promptIndex);
                }
                if (executionId != Guid.Empty) CompleteTrace(executionId);
                throw;
            }
            finally
            {
                try
                {
                    await ownership.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    lock (_sync) _unusable = true;
                    // A known durable commit must remain an accurately reported success. If the turn
                    // itself failed, preserve that original failure rather than masking it with cleanup.
                    // No logging/diagnostic hook exists on this runner; future use fails closed.
                    if (!committed && turnFailure is null)
                        throw new InvalidOperationException("Native session ownership cleanup failed; this runner is unusable.");
                }
            }
        }
    }

    private void PinMode(bool durable, NativeAgentSessionBinding? binding)
    {
        lock (_sync)
        {
            if (_unusable)
                throw new InvalidOperationException("This conversation runner is unusable after native session ownership cleanup failed.");
            if (_durableMode is not null && (_durableMode != durable || (durable && _durableBinding != binding)))
                throw new InvalidOperationException("A conversation runner cannot change persistence mode or durable session binding.");
            _durableMode ??= durable;
            if (durable) _durableBinding ??= binding;
        }
    }

    private static void ValidateExecutionBinding(AgentExecutionContext context, NativeAgentSessionBinding binding)
    {
        if (context.IdentityKind != AgentIdentityMode.ActiveUser ||
            !StringComparer.Ordinal.Equals(context.TenantScope, binding.TenantId.Value.ToString()) ||
            !StringComparer.Ordinal.Equals(context.PrincipalOrWorkloadScope, binding.PrincipalId.Value.ToString()) ||
            context.SessionId != binding.SessionId)
            throw new UnauthorizedAccessException("The execution identity does not match the native session binding.");
    }

    private static async Task EnsureAuthorizedAsync(AgentExecutionContext context, IsExecutionRevoked revoked, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (DateTimeOffset.UtcNow >= context.Deadline || await revoked(context, ct).ConfigureAwait(false))
            throw new UnauthorizedAccessException("The execution is expired or authorization has been revoked.");
    }

    private static async Task ValidateOwnershipAndAuthorizationAsync(INativeSessionTurnOwnership ownership,
        AgentExecutionContext context, IsExecutionRevoked revoked, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await ownership.ValidateOwnershipAsync(ct).ConfigureAwait(false);
        await EnsureAuthorizedAsync(context, revoked, ct).ConfigureAwait(false);
    }

    /// <summary>Exports opaque checkpoint metadata without copying MAF transcript contents.</summary>
    public ConversationCheckpoint ExportCheckpoint(string sessionKey)
    {
        lock (_sync)
        {
            var children = _childHistoryCounts.Select(item =>
                new ChildSessionCheckpoint(item.Key.AgentId, item.Key.Key, item.Value));
            return ConversationCheckpointCodec.Export(_leadAgentId.Value, sessionKey, _turnSequence, children, PinnedCatalogRevision);
        }
    }

    /// <summary>Imports compatible counters into an empty runner; it does not reconstruct MAF-owned sessions.</summary>
    public void ImportCheckpoint(ConversationCheckpoint checkpoint, string sessionKey)
    {
        var validated = ConversationCheckpointCodec.Import(checkpoint, _leadAgentId.Value, sessionKey, PinnedCatalogRevision);
        lock (_sync)
        {
            if (_history.Count != 0 || _trace.Count != 0 || _turnSequence != 0 || _childSessions.Count != 0)
                throw new InvalidOperationException("A checkpoint can only be imported into a new conversation runner.");
            _turnSequence = validated.TurnSequence;
            foreach (var child in validated.ChildSessions)
                _childHistoryCounts.Add((child.AgentId, child.SessionKey), child.HistoryCount);
        }
    }

    /// <summary>Enforces the tenant/application rate gate exactly once per turn or delegation, after validation and before any budget reservation or MAF invocation.</summary>
    /// <remarks>Truly unattributed calls (missing tenant) deny fail-closed with no audit. Cancellation propagates as <see cref="OperationCanceledException"/>; other gate failures deny fail-closed.</remarks>
    private void ThrowIfRateLimited(string? tenant, string application, string executionId, CancellationToken cancellationToken)
    {
        if (_rateLimitGate is null) return;
        try
        {
            if (string.IsNullOrWhiteSpace(tenant))
                throw new InvalidOperationException("Interactive rate limit exceeded (unattributed).");
            var decision = _rateLimitGate.TryAcquire(new RateLimitKey(tenant, application, RateLimitModes.Interactive),
                executionId, cancellationToken);
            if (!decision.Allowed)
                throw new InvalidOperationException($"Interactive rate limit exceeded ({decision.LimitName}).");
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException) { throw; }
        catch (Exception) { throw new InvalidOperationException("Interactive rate limit exceeded."); }
    }

    private void ReserveModelCall(string prompt)
    {
        if (_runLedger is not null && !_runLedger.TryReserveModelCall(Encoding.UTF8.GetByteCount(prompt)))
            throw new InvalidOperationException("run-wide model budget exhausted");
    }

    private async Task<AgentResponse> RunChildAsync(AIAgent agent, Guid executionId, string prompt, CancellationToken cancellationToken)
    {
        try
        {
            return await agent.RunAsync(prompt, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CompleteTrace(executionId);
        }
    }

    private void AddTrace(ExecutionTraceRecord record)
    {
        lock (_sync) _trace.Add(record);
    }

    private void CompleteTrace(Guid id)
    {
        lock (_sync)
        {
            var index = _trace.FindIndex(record => record.Id == id);
            if (index >= 0) _trace[index] = _trace[index] with { CompletedAt = DateTimeOffset.UtcNow };
        }
    }
}
