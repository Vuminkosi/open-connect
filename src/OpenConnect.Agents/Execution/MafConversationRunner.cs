using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenConnect.Abstractions;

namespace OpenConnect.Agents.Execution;

/// <summary>Runs a lead agent across turns and records real delegated agent executions.</summary>
public sealed class MafConversationRunner
{
    private readonly MafRealAgentFactory _factory;
    private readonly AgentId _leadAgentId;
    private readonly List<ChatMessage> _history = [];
    private readonly List<ExecutionTraceRecord> _trace = [];
    private readonly object _sync = new();
    private readonly SemaphoreSlim _turnGate = new(1, 1);

    /// <summary>Creates a conversation runner for the specified lead agent.</summary>
    public MafConversationRunner(MafRealAgentFactory factory, AgentId leadAgentId)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _leadAgentId = leadAgentId;
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

    /// <summary>Runs a turn using the conversation history and appends the actual response.</summary>
    public async Task<AgentResponse> TurnAsync(string prompt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        cancellationToken.ThrowIfCancellationRequested();
        await _turnGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var promptIndex = -1;
        Guid executionId = Guid.Empty;
        try
        {
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

    /// <summary>Runs the requested child agent and records its real execution beneath the supplied lead execution.</summary>
    public Task<AgentResponse> DelegateAsync(Guid parentExecutionId, AgentId childAgentId, string prompt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrEmpty(childAgentId.Value);
        var agent = _factory.CreateAgent(childAgentId);
        var executionId = Guid.NewGuid();
        AddTrace(new ExecutionTraceRecord(executionId, parentExecutionId, childAgentId.Value, "agent", DateTimeOffset.UtcNow, null));
        return RunChildAsync(agent, executionId, prompt, cancellationToken);
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
