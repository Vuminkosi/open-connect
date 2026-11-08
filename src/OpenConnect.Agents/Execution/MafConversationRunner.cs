using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenConnect.Abstractions;

namespace OpenConnect.Agents.Execution;

/// <summary>Runs a lead agent across turns and records real delegated agent executions.</summary>
public sealed class MafConversationRunner
{
    private readonly MafRealAgentFactory _factory;
    private readonly AgentId _leadAgentId;
    private readonly SessionTurnConcurrency _concurrency;
    private readonly RunBudgetLedger? _runLedger;
    private readonly List<ChatMessage> _history = [];
    private readonly List<ExecutionTraceRecord> _trace = [];
    private readonly Dictionary<(AgentId Child, string Key), AgentSession> _childSessions = [];
    private readonly Dictionary<(string AgentId, string Key), int> _childHistoryCounts = [];
    private long _turnSequence;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _turnGate = new(1, 1);

    /// <summary>Creates a conversation runner for the specified lead agent.</summary>
    /// <param name="factory">Factory used to create the lead agent.</param>
    /// <param name="leadAgentId">Identifier of the lead agent.</param>
    /// <param name="concurrency">Policy for overlapping turns. Queue currently has the same FIFO gate-wait behavior as Serialize; bounded-queue semantics are not specified.</param>
    /// <param name="runLedger">Optional shared run-wide budget ledger. Each invocation reserves once; tokens are estimated from prompt UTF-8 bytes, not response tokens.</param>
    public MafConversationRunner(MafRealAgentFactory factory, AgentId leadAgentId,
        SessionTurnConcurrency concurrency = SessionTurnConcurrency.Serialize, RunBudgetLedger? runLedger = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _leadAgentId = leadAgentId;
        if (!Enum.IsDefined(concurrency)) throw new ArgumentOutOfRangeException(nameof(concurrency));
        _concurrency = concurrency;
        _runLedger = runLedger;
    }

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
    public async Task<AgentResponse> TurnAsync(string prompt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        cancellationToken.ThrowIfCancellationRequested();
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
            ReserveModelCall(prompt);
            var agent = _factory.CreateAgent(_leadAgentId);
            executionId = Guid.NewGuid();
            AddTrace(new ExecutionTraceRecord(executionId, null, _leadAgentId.Value, "lead", DateTimeOffset.UtcNow, null));
            lock (_sync)
            {
                promptIndex = _history.Count;
                _history.Add(new ChatMessage(ChatRole.User, prompt));
            }
            ChatMessage[] messages;
            lock (_sync) messages = _history.ToArray();
            var response = await agent.RunAsync(messages, cancellationToken: cancellationToken).ConfigureAwait(false);
            lock (_sync) _history.AddRange(response.Messages);
            lock (_sync) _turnSequence = checked(_turnSequence + 1);
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
    /// <remarks>The host owns key assignment. A key must not be shared across concurrent branches. The map is bounded by this runner's lifetime and is not durable across restarts; durable session ownership belongs to OpenConnect.Sessions. No trace record exists for work that never began: session creation failure creates no record, and every recorded trace is completed by its owning finally.</remarks>
    /// <returns>The child agent response.</returns>
    /// <exception cref="InvalidOperationException">The configured run budget is exhausted.</exception>
    public async Task<AgentResponse> DelegateAsync(Guid parentExecutionId, AgentId childAgentId, string prompt,
        CancellationToken cancellationToken = default, string? childSessionKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrEmpty(childAgentId.Value);
        if (!string.IsNullOrEmpty(childSessionKey) && string.IsNullOrWhiteSpace(childSessionKey))
            throw new ArgumentException("Session key cannot be whitespace.", nameof(childSessionKey));
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
            var createdSession = await agent.CreateSessionAsync().ConfigureAwait(false);
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
            var response = await agent.RunAsync(prompt, session).ConfigureAwait(false);
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

    /// <summary>Exports opaque checkpoint metadata without copying MAF transcript contents.</summary>
    public ConversationCheckpoint ExportCheckpoint(string sessionKey)
    {
        lock (_sync)
        {
            var children = _childHistoryCounts.Select(item =>
                new ChildSessionCheckpoint(item.Key.AgentId, item.Key.Key, item.Value));
            return ConversationCheckpointCodec.Export(_leadAgentId.Value, sessionKey, _turnSequence, children);
        }
    }

    /// <summary>Imports compatible counters into an empty runner; it does not reconstruct MAF-owned sessions.</summary>
    public void ImportCheckpoint(ConversationCheckpoint checkpoint, string sessionKey)
    {
        var validated = ConversationCheckpointCodec.Import(checkpoint, _leadAgentId.Value, sessionKey);
        lock (_sync)
        {
            if (_history.Count != 0 || _trace.Count != 0 || _turnSequence != 0 || _childSessions.Count != 0)
                throw new InvalidOperationException("A checkpoint can only be imported into a new conversation runner.");
            _turnSequence = validated.TurnSequence;
            foreach (var child in validated.ChildSessions)
                _childHistoryCounts.Add((child.AgentId, child.SessionKey), child.HistoryCount);
        }
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
