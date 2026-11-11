using Microsoft.Extensions.AI;
using OpenConnect.Abstractions;

namespace OpenConnect.Agents.Execution;

/// <summary>
/// Wraps an AIFunction to reauthorize every consequential invocation against current grants,
/// task deadline, active revocation callback, and remaining tool budget.
/// </summary>
public sealed class ReauthorizingAIFunction : AIFunction
{
    private readonly AIFunction _inner;
    private readonly ToolId _toolId;
    private readonly ToolImpact _impact;
    private readonly Func<ToolId, bool> _isToolGranted;
    private readonly IsExecutionRevoked _isRevoked;
    private readonly AgentExecutionContext _context;
    private readonly RunBudgetLedger? _ledger;

    /// <summary>Creates a reauthorizing AIFunction wrapper.</summary>
    public ReauthorizingAIFunction(
        AIFunction inner,
        ToolId toolId,
        ToolImpact impact,
        Func<ToolId, bool> isToolGranted,
        IsExecutionRevoked isRevoked,
        AgentExecutionContext context,
        RunBudgetLedger? ledger = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _toolId = toolId;
        _impact = impact;
        _isToolGranted = isToolGranted ?? throw new ArgumentNullException(nameof(isToolGranted));
        _isRevoked = isRevoked ?? throw new ArgumentNullException(nameof(isRevoked));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _ledger = ledger;
    }

    /// <inheritdoc />
    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 1. Task deadline check
        if (DateTimeOffset.UtcNow >= _context.Deadline)
            throw new UnauthorizedAccessException($"Tool '{_toolId.Value}' execution denied: task deadline has expired.");

        // 2. Active revocation check
        if (await _isRevoked(_context, cancellationToken).ConfigureAwait(false))
            throw new UnauthorizedAccessException($"Tool '{_toolId.Value}' execution denied: authorization grant has been revoked.");

        // 3. Current active tool grant check
        if (!_isToolGranted(_toolId))
            throw new UnauthorizedAccessException($"Tool '{_toolId.Value}' execution denied: tool is not in current active grants.");

        // 4. Consequential tool budget check
        if (_ledger != null && !_ledger.TryReserveToolCall())
            throw new UnauthorizedAccessException($"Tool '{_toolId.Value}' execution denied: tool call budget exhausted.");

        // Reauthorization succeeded: invoke underlying tool
        return await _inner.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Factory and extension helpers for wrapping tools with per-call reauthorization gates.</summary>
public static class PerCallToolReauthorizer
{
    /// <summary>Wraps an AIFunction with per-call reauthorization against current grants.</summary>
    public static AIFunction Wrap(
        AIFunction inner,
        ToolId toolId,
        ToolImpact impact,
        Func<ToolId, bool> isToolGranted,
        IsExecutionRevoked isRevoked,
        AgentExecutionContext context,
        RunBudgetLedger? ledger = null)
    {
        return new ReauthorizingAIFunction(inner, toolId, impact, isToolGranted, isRevoked, context, ledger);
    }
}
